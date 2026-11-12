using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Messages;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private static string ReceiptsUrl(Guid conversation) => $"/api/conversations/{conversation}/receipts";
    private static Task<ReceiptPage?> Receipts(HttpClient client, Guid conversation) => client.GetFromJsonAsync<ReceiptPage>(ReceiptsUrl(conversation));

    [Fact]
    public async Task ReceiptsPublishCommittedProgressOnlyAndRecoverMissedEventsFromRest()
    {
        using var owner = Client(); using var member = Client(); using var outsider = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member); var c = await HubLogin(outsider);
        var conversation = await Group(owner, b.UserId);
        var first = (await (await Send(owner, conversation, "first")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var second = (await (await Send(owner, conversation, "second")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var membership = (await Delivery(member, conversation))!.MembershipId;
        await using var ah = HubConnection(a.Cookie); await using var bh = HubConnection(b.Cookie); await using var ch = HubConnection(c.Cookie);
        var events = new ConcurrentQueue<ReceiptAdvanced>(); var others = new ConcurrentQueue<ReceiptAdvanced>(); var own = new ConcurrentQueue<ReceiptAdvanced>();
        using var la = ah.On<ReceiptAdvanced>(ReceiptBroadcaster.EventName, events.Enqueue);
        using var lb = bh.On<ReceiptAdvanced>(ReceiptBroadcaster.EventName, own.Enqueue);
        using var lc = ch.On<ReceiptAdvanced>(ReceiptBroadcaster.EventName, others.Enqueue);
        await Task.WhenAll(ah.StartAsync(), bh.StartAsync(), ch.StartAsync());
        await ah.InvokeAsync("Subscribe", conversation); await bh.InvokeAsync("Subscribe", conversation);
        await Deliver(member, conversation, first.Id, membership);
        await BroadcastBarrier(ah, bh, ch);
        var delivered = Assert.Single(events); Assert.Equal(conversation, delivered.ConversationId);
        Assert.Equal(new ReceiptAnchor(first.Id, first.CreatedAt), delivered.Member.Delivered); Assert.Null(delivered.Member.Read);
        Assert.Equal(b.UserId, delivered.Member.UserId); Assert.Equal(membership, delivered.Member.MembershipId);
        Assert.Single(own); Assert.Empty(others);
        await Advance(member, conversation, second.Id); await BroadcastBarrier(ah, bh, ch);
        Assert.Equal(2, events.Count); var read = events.Last(); Assert.Equal(second.Id, read.Member.Read!.MessageId);
        Assert.Equal(read.Member.Read, read.Member.Delivered);
        await Advance(member, conversation, first.Id); await Deliver(member, conversation, first.Id, membership);
        await Deliver(member, conversation, second.Id, membership); await Advance(member, conversation, second.Id);
        await Problem(await Deliver(outsider, conversation, second.Id, membership), HttpStatusCode.NotFound);
        member.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Problem(await Deliver(member, conversation, second.Id, membership), HttpStatusCode.BadRequest); await Csrf(member);
        await BroadcastBarrier(ah, bh, ch); Assert.Equal(2, events.Count); Assert.Empty(others);
        await ah.InvokeAsync("Unsubscribe", conversation);
        var third = (await (await Send(owner, conversation, "missed")).Content.ReadFromJsonAsync<MessageResponse>())!;
        await Advance(member, conversation, third.Id); await BroadcastBarrier(ah, bh, ch);
        Assert.Equal(2, events.Count);
        var snapshot = (await Receipts(owner, conversation))!;
        Assert.Equal(third.Id, snapshot.Items.Single(x => x.UserId == b.UserId).Read!.MessageId);
        Assert.Null(snapshot.Items.Single(x => x.UserId == a.UserId).Read);
        await Problem(await outsider.GetAsync(ReceiptsUrl(conversation)), HttpStatusCode.NotFound);
        using var anonymous = Client(); await Problem(await anonymous.GetAsync(ReceiptsUrl(conversation)), HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptPublisherSeesCommittedPairAndTransportFailurePreservesSuccess(bool fail)
    {
        var publisher = new CommitCheckingReceiptBroadcaster { Fail = fail };
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IReceiptBroadcaster>(); services.AddSingleton<IReceiptBroadcaster>(publisher); }));
        publisher.Services = factory.Services;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await HubLogin(client); var conversation = await Group(client);
        var message = (await (await Send(client, conversation, "committed")).Content.ReadFromJsonAsync<MessageResponse>())!;
        publisher.Expected = message.Id;
        Assert.Equal(HttpStatusCode.OK, (await Advance(client, conversation, message.Id)).StatusCode);
        Assert.True(publisher.Visible); Assert.Equal(1, publisher.Attempts);
        await Advance(client, conversation, message.Id); Assert.Equal(1, publisher.Attempts);
    }

    private sealed class CommitCheckingReceiptBroadcaster : IReceiptBroadcaster
    {
        public IServiceProvider Services { get; set; } = null!;
        public Guid Expected { get; set; }
        public bool Fail { get; init; }
        public bool Visible { get; private set; }
        public int Attempts { get; private set; }
        public async Task Advanced(Guid conversation, Guid membership, CancellationToken token)
        {
            Attempts++;
            using var scope = Services.CreateScope();
            Visible = await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers.AsNoTracking()
                .AnyAsync(x => x.ConversationId == conversation && x.MembershipId == membership
                    && x.LastReadMessageId == Expected && x.LastDeliveredMessageId == Expected, token);
            if (Fail) throw new InvalidOperationException("Simulated transport failure");
        }
    }

    [Fact]
    public async Task FailedReceiptTransactionRollsBackBothAnchorsAndEmitsNothing()
    {
        using var client = Client(); var actor = await HubLogin(client); var conversation = await Group(client);
        var message = (await (await Send(client, conversation, "rollback")).Content.ReadFromJsonAsync<MessageResponse>())!;
        await using var hub = HubConnection(actor.Cookie); var events = new ConcurrentQueue<ReceiptAdvanced>();
        using var listener = hub.On<ReceiptAdvanced>(ReceiptBroadcaster.EventName, events.Enqueue);
        await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" ADD CONSTRAINT \"CK_Test_ReceiptRollback\" CHECK (\"LastDeliveredMessageId\" IS NULL) NOT VALID");
        try { await Assert.ThrowsAsync<PostgresException>(() => Advance(client, conversation, message.Id)); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" DROP CONSTRAINT \"CK_Test_ReceiptRollback\""); }
        await BroadcastBarrier(hub); Assert.Empty(events);
        var state = Assert.Single((await Receipts(client, conversation))!.Items);
        Assert.Null(state.Read); Assert.Null(state.Delivered);
    }

    [Fact]
    public async Task ReceiptRosterPaginationRevocationAndRejoinKeepIncarnationsSeparate()
    {
        using var owner = Client(); using var member = Client(); var a = await HubLogin(owner); var b = await HubLogin(member);
        var conversation = await Group(owner, b.UserId);
        var message = (await (await Send(owner, conversation, "history")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var initial = (await Receipts(member, conversation))!; var old = initial.MyMembershipId;
        var page1 = (await owner.GetFromJsonAsync<ReceiptPage>(ReceiptsUrl(conversation) + "?limit=1"))!;
        var page2 = (await owner.GetFromJsonAsync<ReceiptPage>(ReceiptsUrl(conversation) + "?limit=1&offset=1"))!;
        Assert.Equal(page1.RosterVersion, page2.RosterVersion); Assert.Equal(1, page1.NextOffset); Assert.Null(page2.NextOffset);
        Assert.Equal(2, page1.Items.Concat(page2.Items).Select(x => x.UserId).Distinct().Count());
        await Problem(await owner.GetAsync(ReceiptsUrl(conversation) + "?limit=101"), HttpStatusCode.BadRequest);
        await using var ah = HubConnection(a.Cookie); await using var bh = HubConnection(b.Cookie);
        var events = new ConcurrentQueue<ReceiptAdvanced>(); var revoked = new ConcurrentQueue<ConversationAccessRevoked>();
        var changed = new ConcurrentQueue<ConversationMembershipChanged>();
        using var receiptListener = bh.On<ReceiptAdvanced>(ReceiptBroadcaster.EventName, events.Enqueue);
        using var revokeListener = bh.On<ConversationAccessRevoked>("ConversationAccessRevoked", revoked.Enqueue);
        using var changeListener = ah.On<ConversationMembershipChanged>("ConversationMembershipChanged", changed.Enqueue);
        await ah.StartAsync(); await bh.StartAsync(); await ah.InvokeAsync("Subscribe", conversation); await bh.InvokeAsync("Subscribe", conversation);
        await Deliver(member, conversation, message.Id, old); await BroadcastBarrier(ah, bh); Assert.Single(events);
        await Leave(member, conversation); await BroadcastBarrier(ah, bh);
        Assert.Equal(new(conversation, old), Assert.Single(revoked)); Assert.Single(changed);
        await Advance(owner, conversation, message.Id); await BroadcastBarrier(ah, bh); Assert.Single(events);
        await Problem(await member.GetAsync(ReceiptsUrl(conversation)), HttpStatusCode.NotFound);
        await Add(owner, conversation, b.UserId);
        var fresh = (await Receipts(member, conversation))!;
        Assert.NotEqual(initial.RosterVersion, fresh.RosterVersion); Assert.NotEqual(old, fresh.MyMembershipId);
        var own = fresh.Items.Single(x => x.UserId == b.UserId); Assert.Null(own.Read); Assert.Null(own.Delivered);
        await bh.InvokeAsync("Subscribe", conversation);
        await fixture.Factory.Services.GetRequiredService<IReceiptBroadcaster>().Advanced(conversation, old, CancellationToken.None);
        await BroadcastBarrier(ah, bh); Assert.Single(events); Assert.Equal(2, changed.Count);
    }
}
