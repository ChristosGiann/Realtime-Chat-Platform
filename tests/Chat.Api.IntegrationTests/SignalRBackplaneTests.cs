using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class SignalRBackplaneTests(MultiInstanceTopology topology) : IClassFixture<MultiInstanceTopology>
{
    private static HttpClient Client(Uri url) => new() { BaseAddress = url };

    private static async Task<(Guid Id, string Cookie)> Login(HttpClient client)
    {
        var name = Guid.NewGuid().ToString("N");
        var email = $"{name}@example.test";
        var created = await client.PostAsJsonAsync("/api/auth/register", new { email, userName = name, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Csrf(client);
        var signed = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.NoContent, signed.StatusCode);
        var cookie = Assert.Single(signed.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Chat.Session=")).Split(';')[0];
        await Csrf(client);
        return (id, cookie);
    }

    private static async Task Csrf(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("requestToken").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }

    private static HubConnection Hub(Uri url, string cookie) => new HubConnectionBuilder()
        .WithUrl(new Uri(url, "/hubs/chat"), options => options.Headers.Add("Cookie", cookie)).Build();

    private static TaskCompletionSource<string> Capture(HubConnection connection, string eventName)
    {
        var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>(eventName, value => result.TrySetResult(value));
        return result;
    }

    [Fact]
    public async Task AuthorizedGroupAndExplicitConnectionsRouteInBothDirections()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl); using var outsider = Client(topology.BUrl);
        var alice = await Login(a); var bob = await Login(b); var eve = await Login(outsider);
        var response = await a.PostAsJsonAsync("/api/conversations/group", new { name = "Backplane probe", memberIds = new[] { bob.Id } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var conversation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var hubA = Hub(topology.AUrl, alice.Cookie);
        await using var hubB = Hub(topology.BUrl, bob.Cookie);
        await using var hubE = Hub(topology.BUrl, eve.Cookie);
        await Task.WhenAll(hubA.StartAsync(), hubB.StartAsync(), hubE.StartAsync());
        Assert.NotNull(hubA.ConnectionId); Assert.NotNull(hubB.ConnectionId);
        await hubA.InvokeAsync("Subscribe", conversation);
        await hubB.InvokeAsync("Subscribe", conversation);
        await Assert.ThrowsAsync<HubException>(() => hubE.InvokeAsync("Subscribe", conversation));
        var fromGroup = Capture(hubB, "TransportGroupProbe");
        await topology.A.Services.GetRequiredService<IHubContext<ChatHub>>().Clients
            .Group(ConversationGroup.Name(conversation)).SendAsync("TransportGroupProbe", "A-to-B");
        Assert.Equal("A-to-B", await fromGroup.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var targetedB = Capture(hubB, "TransportTargetProbe");
        await topology.A.Services.GetRequiredService<IHubContext<ChatHub>>().Clients
            .Client(hubB.ConnectionId!).SendAsync("TransportTargetProbe", "A-to-B");
        Assert.Equal("A-to-B", await targetedB.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var targetedA = Capture(hubA, "TransportTargetProbe");
        await topology.B.Services.GetRequiredService<IHubContext<ChatHub>>().Clients
            .Client(hubA.ConnectionId!).SendAsync("TransportTargetProbe", "B-to-A");
        Assert.Equal("B-to-A", await targetedA.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RedisOutageKeepsCommittedMessageAndRestRecoversAfterReconnection()
    {
        using var a = Client(topology.AUrl); using var b = Client(topology.BUrl);
        var alice = await Login(a); var bob = await Login(b);
        var created = await a.PostAsJsonAsync("/api/conversations/group", new { name = "Outage", memberIds = new[] { bob.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var receiver = Hub(topology.BUrl, bob.Cookie);
        await receiver.StartAsync();
        await receiver.InvokeAsync("Subscribe", conversation);
        var seen = Capture(receiver, "MessageCreated");
        await topology.Redis.PauseAsync();
        Guid messageId;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await a.GetAsync("/health/realtime")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/health/ready")).StatusCode);
            var sent = await a.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
                new { body = "outage committed", clientMessageId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
            messageId = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        finally { await topology.Redis.UnpauseAsync(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await a.GetAsync("/health/realtime", deadline.Token)).StatusCode != HttpStatusCode.OK)
            await Task.Delay(100, deadline.Token);
        var history = await b.GetStringAsync($"/api/conversations/{conversation}/messages");
        Assert.Contains(messageId.ToString(), history, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<TimeoutException>(() => seen.Task.WaitAsync(TimeSpan.FromMilliseconds(800)));
        // Fresh transport can route again after recovery; no replay of the earlier event.
        var fresh = Capture(receiver, "RecoveryProbe");
        await topology.A.Services.GetRequiredService<IHubContext<ChatHub>>().Clients
            .Client(receiver.ConnectionId!).SendAsync("RecoveryProbe", "new-event");
        Assert.Equal("new-event", await fresh.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task DistinctBackplaneRunIdsDoNotCrossDeliver()
    {
        await using var isolated = topology.CreateInstance("isolated", Guid.NewGuid().ToString("N"), backplane: true);
        using var isolatedHttp = isolated.CreateClient();
        using var a = Client(topology.AUrl);
        var alice = await Login(a);
        // Same user/session and durable membership; only the Redis channel namespace differs.
        var response = await a.PostAsJsonAsync("/api/conversations/group", new { name = "Isolated", memberIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var conversation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var hub = Hub(isolatedHttp.BaseAddress!, alice.Cookie);
        await hub.StartAsync();
        await hub.InvokeAsync("Subscribe", conversation);
        var received = Capture(hub, "NamespaceProbe");
        await topology.A.Services.GetRequiredService<IHubContext<ChatHub>>().Clients
            .Group(ConversationGroup.Name(conversation)).SendAsync("NamespaceProbe", "wrong-run");
        var barrier = Capture(hub, "LocalBarrier");
        await isolated.Services.GetRequiredService<IHubContext<ChatHub>>().Clients
            .Client(hub.ConnectionId!).SendAsync("LocalBarrier", "ready");
        Assert.Equal("ready", await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<TimeoutException>(() => received.Task.WaitAsync(TimeSpan.FromMilliseconds(800)));
    }
}
