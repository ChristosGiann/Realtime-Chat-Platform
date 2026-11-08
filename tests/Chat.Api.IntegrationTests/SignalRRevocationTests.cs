using System.Collections.Concurrent;
using System.Net;
using Chat.Api.Messages;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    [Fact]
    public async Task SignalRFailedUnsubscribeRemainsRevocable()
    {
        var groups = new FailingRemovalGroups();
        var registry = new ConversationConnections(new GroupOnlyHubContext(groups));
        var user = Guid.NewGuid(); var conversation = Guid.NewGuid();
        registry.Connected("connection", user, () => { });
        await registry.Subscribe("connection", conversation, CancellationToken.None);
        groups.FailNextRemoval = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.Unsubscribe("connection", conversation, CancellationToken.None));
        Assert.True(groups.Joined);
        await registry.Revoke(conversation, user, CancellationToken.None);
        Assert.False(groups.Joined);
        Assert.Equal(2, groups.RemovalAttempts);
    }

    private sealed class GroupOnlyHubContext(IGroupManager groups) : IHubContext<ChatHub>
    {
        public IHubClients Clients => throw new NotSupportedException();
        public IGroupManager Groups => groups;
    }
    private sealed class FailingRemovalGroups : IGroupManager
    {
        public bool Joined { get; private set; }
        public bool FailNextRemoval { get; set; }
        public int RemovalAttempts { get; private set; }
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Joined = true; return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            RemovalAttempts++;
            if (FailNextRemoval) { FailNextRemoval = false; throw new InvalidOperationException("Simulated group transport failure."); }
            Joined = false; return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalRRemovalRevokesAllTabsAndRequiresExplicitResubscription(bool leave)
    {
        using var owner = Client(); using var member = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member);
        var conversation = await Group(owner, b.UserId); var other = await Group(owner, b.UserId);
        await using var first = HubConnection(b.Cookie); await using var second = HubConnection(b.Cookie); await using var ownerHub = HubConnection(a.Cookie);
        await Task.WhenAll(first.StartAsync(), second.StartAsync(), ownerHub.StartAsync());
        foreach (var hub in new[] { first, second, ownerHub })
        { await hub.InvokeAsync("Subscribe", conversation); await hub.InvokeAsync("Subscribe", other); }
        var received = new ConcurrentQueue<MessageResponse>();
        using var listener = first.On<MessageResponse>(MessageBroadcaster.EventName, received.Enqueue);
        var racingSends = Enumerable.Range(0, 6).Select(i => Send(owner, conversation, $"race {i}")).ToArray();
        var removal = leave ? await member.DeleteAsync($"/api/conversations/{conversation}/membership")
            : await owner.DeleteAsync($"/api/conversations/{conversation}/members/{b.UserId}");
        Assert.Equal(HttpStatusCode.NoContent, removal.StatusCode);
        foreach (var response in await Task.WhenAll(racingSends)) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await BroadcastBarrier(first, second, ownerHub);
        var before = received.Count;
        Assert.Equal(HttpStatusCode.Created, (await Send(owner, conversation, "after removal")).StatusCode);
        await BroadcastBarrier(first, second, ownerHub);
        Assert.Equal(before, received.Count);
        await ProbeGroup(conversation, (first, false), (second, false), (ownerHub, true));
        await ProbeGroup(other, (first, true), (second, true), (ownerHub, true));
        await Assert.ThrowsAsync<HubException>(() => first.InvokeAsync("Subscribe", conversation));
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync($"/api/conversations/{conversation}/members/{b.UserId}", null)).StatusCode);
        await ProbeGroup(conversation, (first, false), (second, false), (ownerHub, true));
        await first.InvokeAsync("Subscribe", conversation);
        await ProbeGroup(conversation, (first, true), (second, false), (ownerHub, true));
    }
}
