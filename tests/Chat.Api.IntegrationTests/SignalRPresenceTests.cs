using System.Collections.Concurrent;
using System.Net;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private static Task<PresencePage> Presence(HubConnection hub, Guid conversation, int offset = 0, int limit = 100) =>
        hub.InvokeAsync<PresencePage>("GetPresence", conversation, offset, limit);

    [Fact]
    public async Task PresenceAggregatesTabsAndPublishesOnlyLogicalTransitions()
    {
        using var owner = Client(); using var member = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member);
        var conversation = await Group(owner, b.UserId);
        await using var observer = HubConnection(a.Cookie); await observer.StartAsync();
        await observer.InvokeAsync("Subscribe", conversation);
        var received = new ConcurrentQueue<ConversationPresenceChanged>();
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = observer.On<ConversationPresenceChanged>(ConversationPresence.EventName, value =>
        {
            if (value.ConversationId != conversation || value.UserId != b.UserId) return;
            received.Enqueue(value); if (!value.IsOnline) offline.TrySetResult();
        });
        Assert.False((await Presence(observer, conversation)).Items.Single(x => x.UserId == b.UserId).IsOnline);
        await using var first = HubConnection(b.Cookie); await using var second = HubConnection(b.Cookie);
        await first.StartAsync(); await Presence(first, conversation);
        await second.StartAsync(); await Presence(second, conversation);
        await BroadcastBarrier(observer);
        Assert.Single(received); Assert.True(received.Single().IsOnline);
        await first.StopAsync();
        Assert.True((await Presence(observer, conversation)).Items.Single(x => x.UserId == b.UserId).IsOnline);
        await second.StopAsync(); await offline.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await Presence(observer, conversation)).Items.Single(x => x.UserId == b.UserId).IsOnline);
        Assert.Equal(new[] { true, false }, received.Select(x => x.IsOnline));
        Assert.True(received.Last().Version > received.First().Version);
    }

    [Fact]
    public async Task PresenceSnapshotIsMemberOnlyPaginatedAndDoesNotRequireSubjectSubscription()
    {
        using var owner = Client(); using var member = Client(); using var outsider = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member); var c = await HubLogin(outsider);
        var conversation = await Group(owner, b.UserId);
        await using var observer = HubConnection(a.Cookie); await using var subject = HubConnection(b.Cookie);
        await using var other = HubConnection(c.Cookie);
        await Task.WhenAll(observer.StartAsync(), subject.StartAsync(), other.StartAsync());
        await Presence(subject, conversation);
        var first = await Presence(observer, conversation, 0, 1);
        Assert.Single(first.Items); Assert.Equal(1, first.NextOffset);
        var second = await Presence(observer, conversation, first.NextOffset!.Value, 1);
        Assert.Null(second.NextOffset); Assert.Single(second.Items);
        Assert.Equal(2, first.Items.Concat(second.Items).Select(x => x.UserId).Distinct().Count());
        Assert.All(first.Items.Concat(second.Items), x => Assert.True(x.IsOnline));
        await Assert.ThrowsAsync<HubException>(() => Presence(other, conversation));
        await Assert.ThrowsAsync<HubException>(() => Presence(observer, Guid.NewGuid()));
        await Assert.ThrowsAsync<HubException>(() => Presence(observer, conversation, -1));
        await Assert.ThrowsAsync<HubException>(() => Presence(observer, conversation, 0, 101));
    }

    [Fact]
    public async Task PresenceRevocationProtectsBothObserverAndSubject()
    {
        using var owner = Client(); using var removed = Client(); using var subject = Client();
        var a = await HubLogin(owner); var b = await HubLogin(removed); var c = await HubLogin(subject);
        var conversation = await Group(owner, b.UserId, c.UserId);
        await using var observer = HubConnection(a.Cookie); await using var former = HubConnection(b.Cookie);
        await Task.WhenAll(observer.StartAsync(), former.StartAsync());
        await observer.InvokeAsync("Subscribe", conversation); await former.InvokeAsync("Subscribe", conversation);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, conversation, b.UserId)).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => Presence(former, conversation));
        var allowed = new ConcurrentQueue<ConversationPresenceChanged>(); var denied = new ConcurrentQueue<ConversationPresenceChanged>();
        using var l1 = observer.On<ConversationPresenceChanged>(ConversationPresence.EventName, allowed.Enqueue);
        using var l2 = former.On<ConversationPresenceChanged>(ConversationPresence.EventName, denied.Enqueue);
        await using var active = HubConnection(c.Cookie); await active.StartAsync(); await Presence(active, conversation);
        await BroadcastBarrier(observer, former);
        Assert.Contains(allowed, x => x.ConversationId == conversation && x.UserId == c.UserId && x.IsOnline);
        Assert.Empty(denied);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, conversation, c.UserId)).StatusCode);
        var before = allowed.Count;
        await active.StopAsync(); await active.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => Presence(active, conversation));
        await BroadcastBarrier(observer, former);
        Assert.Equal(before, allowed.Count); Assert.Empty(denied);
        Assert.DoesNotContain((await Presence(observer, conversation)).Items, x => x.UserId == c.UserId || x.UserId == b.UserId);
    }

    [Fact]
    public async Task PresenceInvalidatedIdentityIsRemovedBeforeInvocationFailure()
    {
        using var owner = Client(); using var member = Client();
        var a = await HubLogin(owner); var b = await HubLogin(member);
        var conversation = await Group(owner, b.UserId);
        await using var observer = HubConnection(a.Cookie); await using var subject = HubConnection(b.Cookie);
        await Task.WhenAll(observer.StartAsync(), subject.StartAsync());
        await Presence(subject, conversation);
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Users.Where(x => x.Id == b.UserId).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, Guid.NewGuid().ToString()));
        await Assert.ThrowsAnyAsync<Exception>(() => Presence(subject, conversation));
        Assert.False((await Presence(observer, conversation)).Items.Single(x => x.UserId == b.UserId).IsOnline);
    }

    [Fact]
    public void PresenceExpiredTicketsAreExcludedEvenBeforeTransportCleanup()
    {
        var registry = new ConversationConnections(new GroupOnlyHubContext(new FailingRemovalGroups()));
        var user = Guid.NewGuid();
        registry.Connected("expired", user, () => { }, DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.False(registry.Snapshot(Guid.NewGuid(), [user], null).Items.Single().IsOnline);
        registry.Connected("valid", user, () => { }, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(registry.Snapshot(Guid.NewGuid(), [user], null).Items.Single().IsOnline);
        Assert.False(registry.Disconnected("valid")!.IsOnline);
    }

    [Fact]
    public async Task PresenceTicketExpirationRemovesLiveConnection()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.ExpireTimeSpan = TimeSpan.FromSeconds(3))));
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var login = await HubLogin(client); var conversation = await Group(client);
        await using var hub = HubConnection(login.Cookie, factory: factory);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync(); Assert.True((await Presence(hub, conversation)).Items.Single().IsOnline);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var registry = factory.Services.GetRequiredService<ConversationConnections>();
        Assert.False(registry.Snapshot(conversation, [login.UserId], null).Items.Single().IsOnline);
    }

    [Fact]
    public async Task PresenceSilentClientTimesOutWithoutAnExtraOfflineGracePeriod()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), fixture.Factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions>>().Value.ClientTimeoutInterval);
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<HubOptions>(options => options.ClientTimeoutInterval = TimeSpan.FromSeconds(1))));
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var login = await HubLogin(client); var conversation = await Group(client);
        await using var hub = HubConnection(login.Cookie, factory: factory);
        hub.KeepAliveInterval = TimeSpan.FromMinutes(1);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync(); Assert.True((await Presence(hub, conversation)).Items.Single().IsOnline);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var registry = factory.Services.GetRequiredService<ConversationConnections>();
        for (var attempt = 0; attempt < 100 && registry.Snapshot(conversation, [login.UserId], null).Items.Single().IsOnline; attempt++)
            await Task.Delay(20);
        Assert.False(registry.Snapshot(conversation, [login.UserId], null).Items.Single().IsOnline);
    }

    [Fact]
    public async Task PresenceQueryRechecksMembershipAfterWaitingForRemovalLock()
    {
        using var owner = Client(); using var member = Client(); await HubLogin(owner); var b = await HubLogin(member);
        var conversation = await Group(owner, b.UserId);
        await using var hub = HubConnection(b.Cookie); await hub.StartAsync(); await Presence(hub, conversation);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleAsync();
        var pending = Presence(hub, conversation);
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
    }
}
