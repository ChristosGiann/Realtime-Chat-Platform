using System.Net;
using System.Net.Http.Json;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private static string ReadUrl(Guid conversation) => $"/api/conversations/{conversation}/read-state";
    private static Task<HttpResponseMessage> Advance(HttpClient client, Guid conversation, Guid messageId) =>
        client.PutAsJsonAsync(ReadUrl(conversation), new { messageId });

    [Fact]
    public async Task ReadStateIsIndependentMonotonicAndCountsOnlyOtherSenders()
    {
        using var alice = Client(); using var bob = Client(); await Login(alice); var b = await Login(bob); var conversation = await Group(alice, b);
        var empty = (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!;
        Assert.Null(empty.LastReadMessageId); Assert.Equal(0, empty.UnreadCount);
        var first = (await (await Send(alice, conversation, "one")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var second = (await (await Send(alice, conversation, "two")).Content.ReadFromJsonAsync<MessageResponse>())!;
        await Send(bob, conversation, "own");
        Assert.Equal(2, (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.UnreadCount);
        Assert.Equal(1, (await alice.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.UnreadCount);
        var partial = (await (await Advance(bob, conversation, first.Id)).Content.ReadFromJsonAsync<ReadStateResponse>())!;
        Assert.Equal(1, partial.UnreadCount); Assert.Equal(first.CreatedAt, partial.LastReadAt);
        var requests = Enumerable.Range(0, 12).Select(i => Advance(bob, conversation, i % 2 == 0 ? first.Id : second.Id));
        Assert.All(await Task.WhenAll(requests), response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        var state = (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!;
        Assert.Equal(second.Id, state.LastReadMessageId); Assert.Equal(0, state.UnreadCount);
        Assert.Equal(1, (await alice.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.UnreadCount);
        await Advance(bob, conversation, first.Id);
        Assert.Equal(state, await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)));
        await Remove(alice, conversation, b);
        await Problem(await bob.GetAsync(ReadUrl(conversation)), HttpStatusCode.NotFound);
        await Problem(await Advance(bob, conversation, first.Id), HttpStatusCode.NotFound);
        await Add(alice, conversation, b);
        var rejoined = (await bob.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!;
        Assert.Null(rejoined.LastReadMessageId); Assert.Equal(2, rejoined.UnreadCount);
    }

    [Fact]
    public async Task ReadStateRejectsSpoofingForeignMessagesAndNonMembers()
    {
        using var owner = Client(); using var outsider = Client(); using var anonymous = Client();
        await Login(owner); await Login(outsider); var conversation = await Group(owner); var other = await Group(owner);
        var message = (await (await Send(owner, other, "foreign")).Content.ReadFromJsonAsync<MessageResponse>())!;
        await Problem(await Advance(owner, conversation, message.Id), HttpStatusCode.NotFound);
        await Problem(await Advance(owner, conversation, Guid.Empty), HttpStatusCode.BadRequest);
        await Problem(await owner.PutAsJsonAsync(ReadUrl(conversation), new { }), HttpStatusCode.BadRequest);
        await Problem(await owner.PutAsJsonAsync(ReadUrl(conversation), new { messageId = message.Id, userId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        await Problem(await outsider.GetAsync(ReadUrl(conversation)), HttpStatusCode.NotFound);
        await Problem(await Advance(outsider, conversation, message.Id), HttpStatusCode.NotFound);
        await Problem(await anonymous.GetAsync(ReadUrl(conversation)), HttpStatusCode.Unauthorized);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Problem(await Advance(owner, other, message.Id), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReadStateUsesUuidTieBreakerAndDatabaseEnforcesConversationBoundary()
    {
        using var client = Client(); var sender = await Login(client); var conversation = await Group(client); var other = await Group(client);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        var time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Messages.AddRange(Enumerable.Range(0, 3).Select(_ => new Message { ConversationId = conversation, SenderId = sender, Body = "tie", CreatedAt = time }));
        await db.SaveChangesAsync();
        var ids = await db.Messages.Where(x => x.ConversationId == conversation).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
        await Advance(client, conversation, ids[2]); await Advance(client, conversation, ids[0]);
        Assert.Equal(ids[2], (await client.GetFromJsonAsync<ReadStateResponse>(ReadUrl(conversation)))!.LastReadMessageId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE conversations.\"ConversationMembers\" SET \"LastReadMessageId\" = {ids[0]} WHERE \"ConversationId\" = {other}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        var deletion = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM messages.\"Messages\" WHERE \"Id\" = {ids[2]}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, deletion.SqlState);
    }
}
