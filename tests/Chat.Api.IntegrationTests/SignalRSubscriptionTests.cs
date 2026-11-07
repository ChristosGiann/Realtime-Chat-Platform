using System.Collections.Concurrent;
using System.Net.Http.Json;
using Chat.Api.Conversations;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    // A barrier sent after the probe proves all prior messages on each connection were processed.
    private async Task ProbeGroup(Guid conversation, params (HubConnection Hub, bool Receives)[] connections)
    {
        var probe = Guid.NewGuid().ToString();
        var listeners = connections.Select(connection =>
        {
            var values = new ConcurrentQueue<string>();
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var receive = connection.Hub.On<string>("SubscriptionProbe", value => values.Enqueue(value));
            var marker = connection.Hub.On<string>("SubscriptionBarrier", value => { if (value == probe) barrier.TrySetResult(); });
            return (connection.Receives, values, barrier, receive, marker);
        }).ToArray();
        try
        {
            var hub = fixture.Factory.Services.GetRequiredService<IHubContext<ChatHub>>();
            await hub.Clients.Group(ConversationGroup.Name(conversation)).SendAsync("SubscriptionProbe", probe);
            await hub.Clients.All.SendAsync("SubscriptionBarrier", probe);
            foreach (var listener in listeners)
            {
                await listener.barrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(listener.Receives ? new[] { probe } : [], listener.values.ToArray());
            }
        }
        finally { foreach (var listener in listeners) { listener.receive.Dispose(); listener.marker.Dispose(); } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalRSubscriptionsAuthorizeMembersAndIsolateConversations(bool direct)
    {
        using var owner = Client(); using var member = Client(); using var outsider = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member); var c = await HubLogin(outsider);
        var conversation = direct ? (await (await Create(owner, b.UserId)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id : await Group(owner, b.UserId);
        var other = await Group(outsider);
        await using var ownerHub = HubConnection(a.Cookie); await using var memberHub = HubConnection(b.Cookie); await using var outsiderHub = HubConnection(c.Cookie);
        await Task.WhenAll(ownerHub.StartAsync(), memberHub.StartAsync(), outsiderHub.StartAsync());
        await ownerHub.InvokeAsync("Subscribe", conversation);
        await memberHub.InvokeAsync("Subscribe", conversation); await memberHub.InvokeAsync("Subscribe", conversation);
        await outsiderHub.InvokeAsync("Subscribe", other);
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync("Subscribe", conversation));
        await Assert.ThrowsAsync<HubException>(() => memberHub.InvokeAsync("Subscribe", other));
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync("Subscribe", conversation, a.UserId));
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync("AddToGroupAsync", ConversationGroup.Name(conversation)));
        await Assert.ThrowsAsync<HubException>(() => memberHub.InvokeAsync("Subscribe", Guid.NewGuid()));
        await Assert.ThrowsAsync<HubException>(() => memberHub.InvokeAsync("Subscribe", Guid.Empty));
        await ProbeGroup(conversation, (ownerHub, true), (memberHub, true), (outsiderHub, false));
        await ProbeGroup(other, (ownerHub, false), (memberHub, false), (outsiderHub, true));
        await memberHub.InvokeAsync("Unsubscribe", conversation); await memberHub.InvokeAsync("Unsubscribe", conversation);
        await outsiderHub.InvokeAsync("Unsubscribe", conversation);
        await ProbeGroup(conversation, (ownerHub, true), (memberHub, false), (outsiderHub, false));
        await memberHub.InvokeAsync("Subscribe", conversation);
        await ProbeGroup(conversation, (ownerHub, true), (memberHub, true), (outsiderHub, false));
        await memberHub.StopAsync(); await memberHub.StartAsync();
        await ProbeGroup(conversation, (ownerHub, true), (memberHub, false), (outsiderHub, false));
    }

    [Fact]
    public async Task SignalRSubscribeRechecksMembershipAfterWaitingForMutationLock()
    {
        using var owner = Client(); using var member = Client(); await HubLogin(owner); var b = await HubLogin(member);
        var conversation = await Group(owner, b.UserId);
        await using var hub = HubConnection(b.Cookie); await hub.StartAsync();
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleAsync();
        var pending = hub.InvokeAsync("Subscribe", conversation);
        using var observerScope = fixture.Factory.Services.CreateScope(); var observer = Db(observerScope);
        var waiting = false;
        for (var attempt = 0; attempt < 100 && !waiting; attempt++)
        {
            waiting = await observer.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync() > 0;
            if (!waiting) await Task.Delay(50);
        }
        Assert.True(waiting);
        await db.ConversationMembers.Where(x => x.ConversationId == conversation && x.UserId == b.UserId).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        await Assert.ThrowsAsync<HubException>(() => pending);
        await hub.InvokeAsync("Unsubscribe", conversation);
        await ProbeGroup(conversation, (hub, false));
    }

    [Fact]
    public async Task SignalRSubscriptionRevalidatesDeletedIdentityOnExistingSocket()
    {
        using var client = Client(); var login = await HubLogin(client);
        await using var hub = HubConnection(login.Cookie); await hub.StartAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Users.Where(x => x.Id == login.UserId).ExecuteDeleteAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => hub.InvokeAsync("Subscribe", Guid.NewGuid()));
    }
}
