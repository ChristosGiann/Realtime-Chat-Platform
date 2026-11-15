using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class DistributedPresenceTests(MultiInstanceTopology topology) : IClassFixture<MultiInstanceTopology>
{
    private static HttpClient Client(Uri url) => new() { BaseAddress = url };

    private static async Task<(Guid Id, string Cookie)> Login(HttpClient client)
    {
        var name = Guid.NewGuid().ToString("N");
        var email = $"{name}@example.test";
        var created = await client.PostAsJsonAsync("/api/auth/register", new { email, userName = name, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("requestToken").GetString();
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        var signed = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.NoContent, signed.StatusCode);
        token = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("requestToken").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
        return (id, Assert.Single(signed.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Chat.Session=")).Split(';')[0]);
    }

    private static HubConnection Hub(Uri url, string cookie) => new HubConnectionBuilder()
        .WithUrl(new Uri(url, "/hubs/chat"), options => options.Headers.Add("Cookie", cookie)).Build();

    private static Task<PresencePage> Presence(HubConnection hub, Guid conversation) =>
        hub.InvokeAsync<PresencePage>("GetPresence", conversation, 0, 100);

    [Fact]
    public async Task TwoInstancesAggregateTabsAndPublishOnlyGlobalTransitions()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var owner = await Login(a); var member = await Login(b);
        var created = await a.PostAsJsonAsync("/api/conversations/group", new { name = "Presence A B", memberIds = new[] { member.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var observer = Hub(topology.AUrl, owner.Cookie);
        await observer.StartAsync(); await observer.InvokeAsync("Subscribe", conversation);
        var routeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var route = observer.On("PresenceTransportReady", () => routeReady.TrySetResult());
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (!routeReady.Task.IsCompleted)
            {
                await topology.B.Services.GetRequiredService<IHubContext<ChatHub>>().Clients
                    .Client(observer.ConnectionId!).SendAsync("PresenceTransportReady", cancellationToken: deadline.Token);
                await Task.WhenAny(routeReady.Task, Task.Delay(200, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
            }
        }
        Assert.False((await Presence(observer, conversation)).Items.Single(x => x.UserId == member.Id).IsOnline);
        var changes = new List<ConversationPresenceChanged>();
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = observer.On<ConversationPresenceChanged>(ConversationPresence.EventName, value =>
        {
            if (value.UserId != member.Id || value.ConversationId != conversation) return;
            lock (changes) changes.Add(value);
            if (value.IsOnline) online.TrySetResult(); else offline.TrySetResult();
        });
        await using var tabB = Hub(topology.BUrl, member.Cookie);
        await using var tabA = Hub(topology.AUrl, member.Cookie);
        await tabB.StartAsync();
        await online.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((await Presence(observer, conversation)).Items.Single(x => x.UserId == member.Id).IsOnline);
        await tabA.StartAsync();
        Assert.True((await Presence(observer, conversation)).Items.Single(x => x.UserId == member.Id).IsOnline);
        await tabB.StopAsync();
        Assert.True((await Presence(observer, conversation)).Items.Single(x => x.UserId == member.Id).IsOnline);
        await tabA.StopAsync();
        await offline.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False((await Presence(observer, conversation)).Items.Single(x => x.UserId == member.Id).IsOnline);
        lock (changes)
        {
            Assert.Equal(new[] { true, false }, changes.Select(x => x.IsOnline));
            Assert.True(changes[1].Version > changes[0].Version);
            Assert.Equal(changes[0].Epoch, changes[1].Epoch);
        }
    }

    [Fact]
    public async Task RedisOutageDoesNotInventOfflineSnapshot()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var owner = await Login(a); var member = await Login(b);
        var created = await a.PostAsJsonAsync("/api/conversations/group", new { name = "Presence outage", memberIds = new[] { member.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var observer = Hub(topology.AUrl, owner.Cookie);
        await using var subject = Hub(topology.BUrl, member.Cookie);
        await Task.WhenAll(observer.StartAsync(), subject.StartAsync());
        Assert.True((await Presence(observer, conversation)).Items.Single(x => x.UserId == member.Id).IsOnline);
        await topology.Redis.PauseAsync();
        try { await Assert.ThrowsAsync<HubException>(() => Presence(observer, conversation)); }
        finally { await topology.Redis.UnpauseAsync(); }
        // Durable membership remains available through PostgreSQL after transport recovery.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await a.GetAsync("/health/realtime", deadline.Token)).StatusCode != HttpStatusCode.OK)
            await Task.Delay(100, deadline.Token);
        var roster = await a.GetStringAsync($"/api/conversations/{conversation}");
        Assert.Contains(conversation.ToString(), roster, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PresenceIsAuthorizedAndIsolatedByRedisNamespace()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var owner = await Login(a); var outsider = await Login(b);
        var created = await a.PostAsJsonAsync("/api/conversations/group", new { name = "Isolated presence", memberIds = new[] { outsider.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var ownerHub = Hub(topology.AUrl, owner.Cookie);
        await using var outsiderHub = Hub(topology.BUrl, outsider.Cookie);
        await Task.WhenAll(ownerHub.StartAsync(), outsiderHub.StartAsync());
        Assert.All((await Presence(ownerHub, conversation)).Items, item => Assert.True(item.IsOnline));
        Assert.Equal(2, (await Presence(outsiderHub, conversation)).Items.Count);
        var beforeRemoval = await ownerHub.InvokeAsync<PresencePage>("GetPresence", conversation, 0, 1);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/conversations/{conversation}/members/{outsider.Id}")).StatusCode);
        var afterRemoval = await Presence(ownerHub, conversation);
        Assert.Single(afterRemoval.Items);
        Assert.NotEqual(beforeRemoval.RosterVersion, afterRemoval.RosterVersion);
        await Assert.ThrowsAsync<HubException>(() => Presence(outsiderHub, conversation));
        await using var isolated = topology.CreateInstance("isolated-presence", Guid.NewGuid().ToString("N"), backplane: true);
        using var isolatedHttp = isolated.CreateClient();
        var isolatedRegistry = isolated.Services.GetRequiredService<DistributedConnectionRegistry>();
        Assert.False((await isolatedRegistry.Snapshot([owner.Id], CancellationToken.None)).Members[owner.Id].IsOnline);
    }

    [Fact]
    public async Task ExpiredTicketCannotBeExtendedByHeartbeat()
    {
        var registry = topology.A.Services.GetRequiredService<DistributedConnectionRegistry>();
        var user = Guid.NewGuid();
        var id = Guid.NewGuid().ToString("N");
        var expiry = DateTimeOffset.UtcNow.AddSeconds(2);
        var registered = await registry.Register(id, user, expiry, () => { }, CancellationToken.None);
        Assert.True(registered.Success);
        Assert.True((await registry.Snapshot([user], CancellationToken.None)).Members[user].IsOnline);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False((await registry.Snapshot([user], CancellationToken.None)).Members[user].IsOnline);
        await registry.Remove(id, user);
    }

    [Fact]
    public async Task NewBootFencesStaleOwnerAfterLeaseExpiry()
    {
        var old = topology.A.Services.GetRequiredService<DistributedConnectionRegistry>();
        await using var replacement = topology.CreateInstance("instance-a", backplane: true);
        using var client = replacement.CreateClient();
        var fresh = replacement.Services.GetRequiredService<DistributedConnectionRegistry>();
        Assert.Equal(old.InstanceId, fresh.InstanceId);
        Assert.NotEqual(old.BootEpoch, fresh.BootEpoch);
        var user = Guid.NewGuid(); var id = Guid.NewGuid().ToString("N");
        Assert.True((await old.Register(id, user, DateTimeOffset.UtcNow.AddSeconds(2), () => { }, CancellationToken.None)).Success);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False((await old.Snapshot([user], CancellationToken.None)).Members[user].IsOnline);
        Assert.True((await fresh.Register(id, user, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
        Assert.False((await old.Register(id, user, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
        await old.Remove(id, user);
        Assert.True((await fresh.Snapshot([user], CancellationToken.None)).Members[user].IsOnline);
        await fresh.Remove(id, user);
        Assert.False((await fresh.Snapshot([user], CancellationToken.None)).Members[user].IsOnline);
    }

    [Fact]
    public async Task LostOwnerExpiresWithoutGracefulDisconnect()
    {
        var observer = topology.B.Services.GetRequiredService<DistributedConnectionRegistry>();
        var user = Guid.NewGuid();
        var id = Guid.NewGuid().ToString("N");
        var crashed = topology.CreateInstance("lost-owner", backplane: true);
        using (var client = crashed.CreateClient())
        {
            var owner = crashed.Services.GetRequiredService<DistributedConnectionRegistry>();
            Assert.True((await owner.Register(id, user, DateTimeOffset.UtcNow.AddMinutes(1), () => { }, CancellationToken.None)).Success);
            Assert.True((await observer.Snapshot([user], CancellationToken.None)).Members[user].IsOnline);
        }
        // This synthetic registry connection has no SignalR disconnect callback; only the process stops.
        await crashed.DisposeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        while ((await observer.Snapshot([user], deadline.Token)).Members[user].IsOnline)
            await Task.Delay(250, deadline.Token);
        Assert.False((await observer.Snapshot([user], deadline.Token)).Members[user].IsOnline);
    }
}
