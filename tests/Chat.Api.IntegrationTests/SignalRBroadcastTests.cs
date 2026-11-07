using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Messages;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private async Task BroadcastBarrier(params HubConnection[] connections)
    {
        var marker = Guid.NewGuid().ToString();
        var listeners = connections.Select(connection =>
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listener = connection.On<string>("BroadcastBarrier", value => { if (value == marker) received.TrySetResult(); });
            return (received, listener);
        }).ToArray();
        try
        {
            await fixture.Factory.Services.GetRequiredService<IHubContext<ChatHub>>().Clients.All.SendAsync("BroadcastBarrier", marker);
            foreach (var item in listeners) await item.received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { foreach (var item in listeners) item.listener.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalRBroadcastsOnlyNewCommittedMessagesToSubscribedConversation(bool direct)
    {
        using var owner = Client(); using var member = Client(); using var outsider = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member); var c = await HubLogin(outsider);
        var conversation = direct ? (await (await Create(owner, b.UserId)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id : await Group(owner, b.UserId);
        var other = await Group(outsider);
        await using var ownerHub = HubConnection(a.Cookie); await using var memberHub = HubConnection(b.Cookie); await using var outsiderHub = HubConnection(c.Cookie);
        var ownerEvents = new ConcurrentQueue<MessageResponse>(); var memberEvents = new ConcurrentQueue<MessageResponse>(); var outsiderEvents = new ConcurrentQueue<MessageResponse>();
        using var ownerListener = ownerHub.On<MessageResponse>(MessageBroadcaster.EventName, ownerEvents.Enqueue);
        using var memberListener = memberHub.On<MessageResponse>(MessageBroadcaster.EventName, memberEvents.Enqueue);
        using var outsiderListener = outsiderHub.On<MessageResponse>(MessageBroadcaster.EventName, outsiderEvents.Enqueue);
        await Task.WhenAll(ownerHub.StartAsync(), memberHub.StartAsync(), outsiderHub.StartAsync());
        await ownerHub.InvokeAsync("Subscribe", conversation); await memberHub.InvokeAsync("Subscribe", conversation); await outsiderHub.InvokeAsync("Subscribe", other);
        var key = Guid.NewGuid();
        var sends = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendKey(owner, conversation, key, "  committed 😀  ")));
        Assert.Single(sends, x => x.StatusCode == HttpStatusCode.Created); Assert.Equal(7, sends.Count(x => x.StatusCode == HttpStatusCode.OK));
        var saved = (await sends.Single(x => x.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<MessageResponse>())!;
        await BroadcastBarrier(ownerHub, memberHub, outsiderHub);
        Assert.Equal(saved, Assert.Single(ownerEvents)); Assert.Equal(saved, Assert.Single(memberEvents)); Assert.Empty(outsiderEvents);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        Assert.Equal(saved.Body, (await db.Messages.AsNoTracking().SingleAsync(x => x.Id == saved.Id)).Body);
        Assert.Equal(HttpStatusCode.OK, (await SendKey(owner, conversation, key, saved.Body)).StatusCode);
        await Problem(await SendKey(owner, conversation, key, "conflict"), HttpStatusCode.Conflict);
        await Problem(await Send(owner, conversation, "  "), HttpStatusCode.BadRequest);
        await Problem(await Send(outsider, conversation, "forbidden"), HttpStatusCode.NotFound);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Problem(await Send(owner, conversation, "csrf"), HttpStatusCode.BadRequest); await Csrf(owner);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE messages.\"Messages\" ADD CONSTRAINT \"CK_Test_BroadcastRollback\" CHECK (\"Body\" <> 'broadcast-rollback') NOT VALID");
        try { await Assert.ThrowsAsync<DbUpdateException>(() => Send(owner, conversation, "broadcast-rollback")); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE messages.\"Messages\" DROP CONSTRAINT \"CK_Test_BroadcastRollback\""); }
        await BroadcastBarrier(ownerHub, memberHub, outsiderHub);
        Assert.Single(ownerEvents); Assert.Single(memberEvents); Assert.Empty(outsiderEvents);
        Assert.Equal(1, await db.Messages.CountAsync(x => x.ConversationId == conversation));
        await memberHub.InvokeAsync("Unsubscribe", conversation);
        Assert.Equal(HttpStatusCode.Created, (await Send(owner, conversation, "another logical send")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Send(outsider, other, "other conversation")).StatusCode);
        await BroadcastBarrier(ownerHub, memberHub, outsiderHub);
        Assert.Equal(2, ownerEvents.Count); Assert.Single(memberEvents);
        Assert.Equal(other, Assert.Single(outsiderEvents).ConversationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalRPublisherSeesCommittedRow_AndDeliveryFailurePreservesRestSuccess(bool failDelivery)
    {
        var publisher = new CommitCheckingBroadcaster { FailDelivery = failDelivery };
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMessageBroadcaster>(); services.AddSingleton<IMessageBroadcaster>(publisher);
        }));
        publisher.Services = factory.Services;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        await HubLogin(client); var conversation = await Group(client); var key = Guid.NewGuid();
        var created = await SendKey(client, conversation, key, "Commit first");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<MessageResponse>())!;
        Assert.True(publisher.VisibleFromIndependentContext); Assert.Equal(1, publisher.Attempts);
        var retry = await SendKey(client, conversation, key, "Commit first");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode); Assert.Equal(saved, await retry.Content.ReadFromJsonAsync<MessageResponse>());
        Assert.Equal(1, publisher.Attempts);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Messages.CountAsync(x => x.ClientMessageId == key));
    }

    private sealed class CommitCheckingBroadcaster : IMessageBroadcaster
    {
        public IServiceProvider Services { get; set; } = null!;
        public bool FailDelivery { get; init; }
        public bool VisibleFromIndependentContext { get; private set; }
        public int Attempts { get; private set; }
        public async Task MessageCreatedAsync(MessageResponse message, CancellationToken cancellationToken)
        {
            Attempts++;
            using var scope = Services.CreateScope();
            VisibleFromIndependentContext = await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Messages.AsNoTracking()
                .AnyAsync(x => x.Id == message.Id && x.ClientMessageId == message.ClientMessageId, cancellationToken);
            if (FailDelivery) throw new InvalidOperationException("Simulated transport outage.");
        }
    }
}
