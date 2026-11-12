using System.Collections.Concurrent;
using System.Net;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private sealed class TypingClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }
    private static Task<TypingSnapshot> Typing(HubConnection hub, Guid conversation) => hub.InvokeAsync<TypingSnapshot>("GetTyping", conversation);

    [Fact]
    public async Task TypingAggregatesTabsAndExpiresWithoutStopUsingMonotonicLease()
    {
        var clock = new TypingClock();
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var owner = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var member = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var a = await HubLogin(owner); var b = await HubLogin(member); var conversation = await Group(owner, b.UserId);
        await using var observer = HubConnection(a.Cookie, factory: factory);
        await using var first = HubConnection(b.Cookie, factory: factory); await using var second = HubConnection(b.Cookie, factory: factory);
        foreach (var hub in new[] { observer, first, second }) { await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation); }
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = observer.On<TypingChanged>(ConversationTyping.EventName, value =>
        { if (value.UserId == b.UserId && value.ExpiresInMilliseconds == 0) stopped.TrySetResult(); });
        await first.InvokeAsync("SetTyping", conversation, true);
        Assert.Equal(5000, Assert.Single((await Typing(observer, conversation)).Items).ExpiresInMilliseconds);
        clock.Advance(TimeSpan.FromSeconds(2)); await second.InvokeAsync("SetTyping", conversation, true);
        await first.InvokeAsync("SetTyping", conversation, false);
        var active = Assert.Single((await Typing(observer, conversation)).Items);
        Assert.Equal(b.UserId, active.UserId); Assert.Equal(5000, active.ExpiresInMilliseconds);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1000, Assert.Single((await Typing(observer, conversation)).Items).ExpiresInMilliseconds);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty((await Typing(observer, conversation)).Items);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TypingSpamDoesNotRenewLeaseAndStopBypassesThrottle()
    {
        var clock = new TypingClock();
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var user = await HubLogin(client); var conversation = await Group(client);
        await using var hub = HubConnection(user.Cookie, factory: factory); await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation);
        await hub.InvokeAsync("SetTyping", conversation, true);
        for (var i = 0; i < 9; i++) { clock.Advance(TimeSpan.FromMilliseconds(100)); await hub.InvokeAsync("SetTyping", conversation, true); }
        Assert.Equal(4100, Assert.Single((await Typing(hub, conversation)).Items).ExpiresInMilliseconds);
        await hub.InvokeAsync("SetTyping", conversation, false);
        await hub.InvokeAsync("SetTyping", conversation, true);
        Assert.Empty((await Typing(hub, conversation)).Items);
        clock.Advance(TimeSpan.FromMilliseconds(100)); await hub.InvokeAsync("SetTyping", conversation, true);
        Assert.Single((await Typing(hub, conversation)).Items);
        clock.Advance(TimeSpan.FromSeconds(5)); Assert.Empty((await Typing(hub, conversation)).Items);
    }

    [Fact]
    public async Task TypingRequiresCurrentMemberAndSubscriptionAndRevocationCannotResurrectLease()
    {
        using var owner = Client(); using var member = Client(); using var outsider = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member); var c = await HubLogin(outsider);
        var conversation = await Group(owner, b.UserId);
        await using var observer = HubConnection(a.Cookie); await using var actor = HubConnection(b.Cookie); await using var other = HubConnection(c.Cookie);
        await Task.WhenAll(observer.StartAsync(), actor.StartAsync(), other.StartAsync());
        await observer.InvokeAsync("Subscribe", conversation);
        await Assert.ThrowsAsync<HubException>(() => actor.InvokeAsync("SetTyping", conversation, true));
        await Assert.ThrowsAsync<HubException>(() => Typing(actor, conversation));
        await Assert.ThrowsAsync<HubException>(() => other.InvokeAsync("SetTyping", conversation, true));
        await Assert.ThrowsAsync<HubException>(() => Typing(other, conversation));
        await actor.InvokeAsync("Subscribe", conversation);
        await Assert.ThrowsAsync<HubException>(() => actor.InvokeAsync("SetTyping", conversation, true, a.UserId));
        var denied = new ConcurrentQueue<TypingChanged>();
        using var listener = other.On<TypingChanged>(ConversationTyping.EventName, denied.Enqueue);
        await actor.InvokeAsync("SetTyping", conversation, true);
        Assert.Equal(b.UserId, Assert.Single((await Typing(observer, conversation)).Items).UserId);
        await BroadcastBarrier(observer, actor, other); Assert.Empty(denied);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, conversation, b.UserId)).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => actor.InvokeAsync("SetTyping", conversation, true));
        Assert.Empty((await Typing(observer, conversation)).Items);
        var former = new ConcurrentQueue<TypingChanged>();
        using var formerListener = actor.On<TypingChanged>(ConversationTyping.EventName, former.Enqueue);
        await observer.InvokeAsync("SetTyping", conversation, true);
        await BroadcastBarrier(observer, actor); Assert.Empty(former);
        await observer.InvokeAsync("SetTyping", conversation, false);
        Assert.Equal(HttpStatusCode.NoContent, (await Add(owner, conversation, b.UserId)).StatusCode);
        await actor.InvokeAsync("Subscribe", conversation);
        Assert.Empty((await Typing(observer, conversation)).Items);
    }

    [Fact]
    public async Task TypingUnsubscribeAndReconnectDiscardOldSubscriptionLease()
    {
        using var client = Client(); var user = await HubLogin(client); var conversation = await Group(client);
        await using var hub = HubConnection(user.Cookie); await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation);
        await hub.InvokeAsync("SetTyping", conversation, true);
        await hub.InvokeAsync("Unsubscribe", conversation); await hub.InvokeAsync("Subscribe", conversation);
        Assert.Empty((await Typing(hub, conversation)).Items);
        await hub.StopAsync(); await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => Typing(hub, conversation));
        await hub.InvokeAsync("Subscribe", conversation);
        Assert.Empty((await Typing(hub, conversation)).Items);
    }
}
