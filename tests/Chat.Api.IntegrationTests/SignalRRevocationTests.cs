using System.Collections.Concurrent;
using System.Net;
using Chat.Api.Messages;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
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
