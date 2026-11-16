using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class DistributedTypingTests(MultiInstanceTopology topology) : IClassFixture<MultiInstanceTopology>
{
    private static HttpClient Client(Uri url) => new() { BaseAddress = url };
    private static async Task<(Guid Id, string Cookie)> Login(HttpClient client)
    {
        var name = Guid.NewGuid().ToString("N"); var email = $"{name}@example.test";
        var created = await client.PostAsJsonAsync("/api/auth/register", new { email, userName = name, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Csrf(client);
        var signed = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.NoContent, signed.StatusCode);
        var cookie = Assert.Single(signed.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Chat.Session=")).Split(';')[0];
        await Csrf(client);
        return (id, cookie);
    }
    private static async Task Csrf(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("requestToken").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    private static HubConnection Hub(Uri url, string cookie) => new HubConnectionBuilder()
        .WithUrl(new Uri(url, "/hubs/chat"), options => options.Headers.Add("Cookie", cookie)).Build();
    private static Task<TypingSnapshot> Typing(HubConnection hub, Guid conversation) => hub.InvokeAsync<TypingSnapshot>("GetTyping", conversation);
    private static async Task<Guid> Group(HttpClient owner, params Guid[] members)
    {
        var created = await owner.PostAsJsonAsync("/api/conversations/group", new { name = "Distributed typing", memberIds = members });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task RouteReady(HubConnection target, IHubContext<ChatHub> from)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = target.On("TypingRouteReady", () => ready.TrySetResult());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready.Task.IsCompleted)
        {
            await from.Clients.Client(target.ConnectionId!).SendAsync("TypingRouteReady", cancellationToken: deadline.Token);
            await Task.WhenAny(ready.Task, Task.Delay(200, deadline.Token));
            deadline.Token.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task AToBAndMultipleTabsAggregateUntilLastStop()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var actor = await Login(a); var observerUser = await Login(b);
        var conversation = await Group(a, observerUser.Id);
        await using var observer = Hub(topology.BUrl, observerUser.Cookie);
        await using var tabA = Hub(topology.AUrl, actor.Cookie);
        await using var tabB = Hub(topology.BUrl, actor.Cookie);
        foreach (var hub in new[] { observer, tabA, tabB }) { await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation); }
        await RouteReady(observer, topology.A.Services.GetRequiredService<IHubContext<ChatHub>>());
        var changes = new List<TypingChanged>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = observer.On<TypingChanged>(ConversationTyping.EventName, change =>
        {
            if (change.UserId != actor.Id || change.ConversationId != conversation) return;
            lock (changes) changes.Add(change);
            if (change.ExpiresInMilliseconds > 0) started.TrySetResult(); else stopped.TrySetResult();
        });
        await tabA.InvokeAsync("SetTyping", conversation, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(actor.Id, Assert.Single((await Typing(observer, conversation)).Items).UserId);
        await tabB.InvokeAsync("SetTyping", conversation, true);
        Assert.Single((await Typing(observer, conversation)).Items);
        await tabA.InvokeAsync("SetTyping", conversation, false);
        Assert.Single((await Typing(observer, conversation)).Items);
        await tabB.InvokeAsync("SetTyping", conversation, false);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty((await Typing(observer, conversation)).Items);
        lock (changes) Assert.Contains(changes, x => x.ExpiresInMilliseconds > 0);
    }

    [Fact]
    public async Task ExpiryAndUnsubscribeRejoinFenceOldLease()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var actor = await Login(a); var observerUser = await Login(b);
        var conversation = await Group(a, observerUser.Id);
        await using var observer = Hub(topology.BUrl, observerUser.Cookie);
        await using var sender = Hub(topology.AUrl, actor.Cookie);
        foreach (var hub in new[] { observer, sender }) { await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation); }
        await sender.InvokeAsync("SetTyping", conversation, true);
        Assert.Single((await Typing(observer, conversation)).Items);
        await sender.InvokeAsync("Unsubscribe", conversation);
        await sender.InvokeAsync("Subscribe", conversation);
        Assert.Empty((await Typing(observer, conversation)).Items);
        await sender.InvokeAsync("SetTyping", conversation, true);
        Assert.Single((await Typing(observer, conversation)).Items);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while ((await Typing(observer, conversation)).Items.Count != 0)
            await Task.Delay(100, deadline.Token);
        Assert.Empty((await Typing(observer, conversation)).Items);
    }

    [Fact]
    public async Task NonMemberCannotSeeOrPublishAndRemovalStopsFutureExposure()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var actor = await Login(a); var member = await Login(b); var outsider = await Login(b);
        var conversation = await Group(a, member.Id);
        await using var sender = Hub(topology.AUrl, actor.Cookie);
        await using var observer = Hub(topology.BUrl, member.Cookie);
        await using var denied = Hub(topology.BUrl, outsider.Cookie);
        foreach (var hub in new[] { sender, observer, denied }) await hub.StartAsync();
        await sender.InvokeAsync("Subscribe", conversation); await observer.InvokeAsync("Subscribe", conversation);
        await Assert.ThrowsAsync<HubException>(() => denied.InvokeAsync("Subscribe", conversation));
        await Assert.ThrowsAsync<HubException>(() => Typing(denied, conversation));
        await Assert.ThrowsAsync<HubException>(() => denied.InvokeAsync("SetTyping", conversation, true));
        var exposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = denied.On<TypingChanged>(ConversationTyping.EventName, _ => exposed.TrySetResult());
        await sender.InvokeAsync("SetTyping", conversation, true);
        Assert.Single((await Typing(observer, conversation)).Items);
        await Assert.ThrowsAsync<TimeoutException>(() => exposed.Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/conversations/{conversation}/members/{member.Id}")).StatusCode);
        await Assert.ThrowsAsync<HubException>(() => Typing(observer, conversation));
    }

    [Fact]
    public async Task LostOwnerLeaseExpiresAndNewBootFencesStaleActivity()
    {
        var observerRegistry = topology.B.Services.GetRequiredService<DistributedConnectionRegistry>();
        var observerTyping = topology.B.Services.GetRequiredService<DistributedTyping>();
        var actor = Guid.NewGuid(); var observer = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var actorMembership = Guid.NewGuid(); var observerMembership = Guid.NewGuid();
        var observerConnection = Guid.NewGuid().ToString("N"); var actorConnection = Guid.NewGuid().ToString("N");
        Assert.True((await observerRegistry.Register(observerConnection, observer, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
        await observerRegistry.Subscribe(observerConnection, conversation, observerMembership, 1, CancellationToken.None);
        var crashed = topology.CreateInstance("typing-owner", backplane: true);
        string oldBoot;
        using (var client = crashed.CreateClient())
        {
            var oldRegistry = crashed.Services.GetRequiredService<DistributedConnectionRegistry>();
            oldBoot = oldRegistry.BootEpoch;
            var oldTyping = crashed.Services.GetRequiredService<DistributedTyping>();
            Assert.True((await oldRegistry.Register(actorConnection, actor, DateTimeOffset.UtcNow.AddSeconds(2), () => { }, CancellationToken.None)).Success);
            await oldRegistry.Subscribe(actorConnection, conversation, actorMembership, 1, CancellationToken.None);
            Assert.NotNull(await oldTyping.Change(actorConnection, conversation, actor, actorMembership, 1, true, CancellationToken.None));
            Assert.Single((await observerTyping.Snapshot(conversation,
                new Dictionary<Guid, Guid> { [actor] = actorMembership, [observer] = observerMembership },
                observerConnection, observer, 1, CancellationToken.None)).Items);
        }
        // No Hub disconnect: graceful host shutdown fences synthetic connections and owner records.
        await crashed.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while ((await observerTyping.Snapshot(conversation,
            new Dictionary<Guid, Guid> { [actor] = actorMembership, [observer] = observerMembership },
            observerConnection, observer, 1, deadline.Token)).Items.Count != 0)
            await Task.Delay(100, deadline.Token);
        await using var replacement = topology.CreateInstance("typing-owner", backplane: true);
        using var replacementClient = replacement.CreateClient();
        var freshRegistry = replacement.Services.GetRequiredService<DistributedConnectionRegistry>();
        var freshTyping = replacement.Services.GetRequiredService<DistributedTyping>();
        Assert.NotEqual(oldBoot, freshRegistry.BootEpoch);
        Assert.True((await freshRegistry.Register(actorConnection, actor, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
        await freshRegistry.Subscribe(actorConnection, conversation, actorMembership, 2, CancellationToken.None);
        Assert.NotNull(await freshTyping.Change(actorConnection, conversation, actor, actorMembership, 2, true, CancellationToken.None));
        Assert.Single((await observerTyping.Snapshot(conversation,
            new Dictionary<Guid, Guid> { [actor] = actorMembership, [observer] = observerMembership },
            observerConnection, observer, 1, CancellationToken.None)).Items);
        await freshRegistry.Remove(actorConnection, actor);
        await observerRegistry.Remove(observerConnection, observer);
    }

    [Fact]
    public async Task RedisOutageIsUnknownAndRecoveryDoesNotReplayTyping()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var actor = await Login(a); var observerUser = await Login(b);
        var conversation = await Group(a, observerUser.Id);
        await using var sender = Hub(topology.AUrl, actor.Cookie);
        await using var observer = Hub(topology.BUrl, observerUser.Cookie);
        foreach (var hub in new[] { sender, observer }) { await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation); }
        await sender.InvokeAsync("SetTyping", conversation, true);
        Assert.Single((await Typing(observer, conversation)).Items);
        await topology.Redis.PauseAsync();
        try { await Assert.ThrowsAsync<HubException>(() => Typing(observer, conversation)); }
        finally { await topology.Redis.UnpauseAsync(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await a.GetAsync("/health/realtime", deadline.Token)).StatusCode != HttpStatusCode.OK)
            await Task.Delay(100, deadline.Token);
        await using var restoredSender = Hub(topology.AUrl, actor.Cookie);
        await using var restoredObserver = Hub(topology.BUrl, observerUser.Cookie);
        foreach (var hub in new[] { restoredSender, restoredObserver }) { await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation); }
        Assert.Empty((await Typing(restoredObserver, conversation)).Items);
        await restoredSender.InvokeAsync("SetTyping", conversation, true);
        Assert.Single((await Typing(restoredObserver, conversation)).Items);
    }

    [Fact]
    public async Task StaleBootCannotOverwriteNewConnectionTyping()
    {
        var oldRegistry = topology.A.Services.GetRequiredService<DistributedConnectionRegistry>();
        var oldTyping = topology.A.Services.GetRequiredService<DistributedTyping>();
        await using var newHost = topology.CreateInstance("instance-a", backplane: true);
        using var client = newHost.CreateClient();
        var freshRegistry = newHost.Services.GetRequiredService<DistributedConnectionRegistry>();
        var freshTyping = newHost.Services.GetRequiredService<DistributedTyping>();
        Assert.Equal(oldRegistry.InstanceId, freshRegistry.InstanceId);
        Assert.NotEqual(oldRegistry.BootEpoch, freshRegistry.BootEpoch);
        var user = Guid.NewGuid(); var conversation = Guid.NewGuid(); var membership = Guid.NewGuid();
        var id = Guid.NewGuid().ToString("N");
        Assert.True((await oldRegistry.Register(id, user, DateTimeOffset.UtcNow.AddSeconds(2), () => { }, CancellationToken.None)).Success);
        await oldRegistry.Subscribe(id, conversation, membership, 1, CancellationToken.None);
        Assert.NotNull(await oldTyping.Change(id, conversation, user, membership, 1, true, CancellationToken.None));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.True((await freshRegistry.Register(id, user, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
        await freshRegistry.Subscribe(id, conversation, membership, 2, CancellationToken.None);
        Assert.NotNull(await freshTyping.Change(id, conversation, user, membership, 2, true, CancellationToken.None));
        await Assert.ThrowsAsync<HubException>(() => oldTyping.Change(id, conversation, user, membership, 1, true, CancellationToken.None));
        await oldRegistry.Remove(id, user);
        await freshRegistry.Remove(id, user);
    }

    [Fact]
    public async Task DelayedOldSubscriptionStartCannotResurrectAfterRejoin()
    {
        var registry = topology.A.Services.GetRequiredService<DistributedConnectionRegistry>();
        var typing = topology.A.Services.GetRequiredService<DistributedTyping>();
        var user = Guid.NewGuid(); var conversation = Guid.NewGuid(); var membership = Guid.NewGuid();
        var id = Guid.NewGuid().ToString("N");
        Assert.True((await registry.Register(id, user, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
        await registry.Subscribe(id, conversation, membership, 1, CancellationToken.None);
        Assert.NotNull(await typing.Change(id, conversation, user, membership, 1, true, CancellationToken.None));
        await registry.Unsubscribe(id, conversation, CancellationToken.None);
        await registry.Subscribe(id, conversation, membership, 2, CancellationToken.None);
        await Assert.ThrowsAsync<HubException>(() => typing.Change(id, conversation, user, membership, 1, true, CancellationToken.None));
        var snapshot = await typing.Snapshot(conversation, new Dictionary<Guid, Guid> { [user] = membership },
            id, user, 2, CancellationToken.None);
        Assert.Empty(snapshot.Items);
        await registry.Remove(id, user);
    }

    [Fact]
    public async Task RedisThrottleDoesNotRenewLeaseAndStopBypassesIt()
    {
        var registry = topology.A.Services.GetRequiredService<DistributedConnectionRegistry>();
        var typing = topology.A.Services.GetRequiredService<DistributedTyping>();
        var user = Guid.NewGuid(); var conversation = Guid.NewGuid(); var membership = Guid.NewGuid();
        var id = Guid.NewGuid().ToString("N");
        Assert.True((await registry.Register(id, user, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
        await registry.Subscribe(id, conversation, membership, 1, CancellationToken.None);
        var activityId = Guid.NewGuid();
        var first = await typing.Change(id, conversation, user, membership, 1, true, CancellationToken.None, activityId);
        Assert.NotNull(first);
        Assert.Null(await typing.Change(id, conversation, user, membership, 1, true, CancellationToken.None));
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        Assert.Null(await typing.Change(id, conversation, user, membership, 1, true, CancellationToken.None, activityId));
        Assert.True(Assert.Single((await typing.Snapshot(conversation, new Dictionary<Guid, Guid> { [user] = membership },
            id, user, 1, CancellationToken.None)).Items).ExpiresInMilliseconds < 4000);
        var stop = await typing.Change(id, conversation, user, membership, 1, false, CancellationToken.None);
        Assert.NotNull(stop); Assert.Equal(0, stop.ExpiresInMilliseconds);
        Assert.NotNull(await typing.Change(id, conversation, user, membership, 1, true, CancellationToken.None));
        Assert.Single((await typing.Snapshot(conversation, new Dictionary<Guid, Guid> { [user] = membership },
            id, user, 1, CancellationToken.None)).Items);
        await registry.Remove(id, user);
    }
}
