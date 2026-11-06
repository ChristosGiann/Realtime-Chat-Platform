using System.Net;
using System.Net.Http.Json;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    [Fact]
    public async Task Stage3_DurableSendRetryHistoryReadAndMembershipLifecycle()
    {
        using var alice = Client(); using var bob = Client(); using var outsider = Client();
        await Login(alice); var b = await Login(bob); await Login(outsider); var conversation = await Group(alice, b);
        var key = Guid.NewGuid();
        var sends = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendKey(alice, conversation, key, "Durable")));
        Assert.Single(sends, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, sends.Count(x => x.StatusCode == HttpStatusCode.OK));
        var saved = (await sends[0].Content.ReadFromJsonAsync<MessageResponse>())!;
        var reply = (await (await Send(bob, conversation, "Reply")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var page = (await bob.GetFromJsonAsync<MessageHistoryPage>(HistoryUrl(conversation, 1)))!;
        Assert.Equal(reply.Id, Assert.Single(page.Items).Id);
        var older = (await bob.GetFromJsonAsync<MessageHistoryPage>(HistoryUrl(conversation, 1, page.NextCursor)))!;
        Assert.Equal(saved.Id, Assert.Single(older.Items).Id); Assert.Null(older.NextCursor);
        Assert.Equal(1, (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.UnreadCount);
        await Advance(bob, conversation, saved.Id);
        Assert.Equal(0, (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.UnreadCount);
        Assert.Equal(1, (await alice.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.UnreadCount);
        await Problem(await outsider.GetAsync(HistoryUrl(conversation)), HttpStatusCode.NotFound);
        await Problem(await Send(outsider, conversation, "Forbidden"), HttpStatusCode.NotFound);
        await Remove(alice, conversation, b);
        await Problem(await Advance(bob, conversation, reply.Id), HttpStatusCode.NotFound);
        await Add(alice, conversation, b);
        Assert.Equal(1, (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.UnreadCount);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        Assert.Equal(2, await db.Messages.CountAsync(x => x.ConversationId == conversation));
        Assert.Equal(1, await db.Messages.CountAsync(x => x.ClientMessageId == key));
        Assert.Null(await db.ConversationMembers.Where(x => x.ConversationId == conversation && x.UserId == b).Select(x => x.LastReadMessageId).SingleAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
