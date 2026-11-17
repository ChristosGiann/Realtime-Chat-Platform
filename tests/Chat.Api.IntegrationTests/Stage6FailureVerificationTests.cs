using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Chat.Api.IntegrationTests;

public sealed class Stage6FailureVerificationTests(MultiInstanceTopology topology, ITestOutputHelper output)
    : IClassFixture<MultiInstanceTopology>
{
    private static HttpClient Client(Uri url) => new() { BaseAddress = url };

    private static async Task<(Guid Id, string Cookie)> Login(HttpClient client)
    {
        var name = Guid.NewGuid().ToString("N");
        var email = $"{name}@example.test";
        var created = await client.PostAsJsonAsync("/api/auth/register",
            new { email, userName = name, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Csrf(client);
        var signed = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.NoContent, signed.StatusCode);
        var cookie = Assert.Single(signed.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Chat.Session=")).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
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

    [Fact]
    public async Task RestartAChangesBootEpochWhileBServesDurableHistoryAndFreshSubscription()
    {
        using var a = Client(topology.AUrl);
        using var b = Client(topology.BUrl);
        var alice = await Login(a);
        var bob = await Login(b);
        var response = await a.PostAsJsonAsync("/api/conversations/group",
            new { name = "Restart verification", memberIds = new[] { bob.Id } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var conversation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var oldEpoch = topology.A.Services.GetRequiredService<DistributedConnectionRegistry>().BootEpoch;
        await using var oldHub = Hub(topology.AUrl, alice.Cookie);
        await oldHub.StartAsync();
        await oldHub.InvokeAsync("Subscribe", conversation);
        var oldConnectionId = oldHub.ConnectionId;
        await topology.RestartAAsync();
        var fresh = topology.A.Services.GetRequiredService<DistributedConnectionRegistry>();
        Assert.NotEqual(oldEpoch, fresh.BootEpoch);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/health/ready")).StatusCode);
        var sent = await b.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { body = "survives API restart", clientMessageId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var id = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var newA = Client(topology.AUrl);
        newA.DefaultRequestHeaders.Add("Cookie", alice.Cookie);
        Assert.Contains(id.ToString(), await newA.GetStringAsync($"/api/conversations/{conversation}/messages"),
            StringComparison.OrdinalIgnoreCase);
        await Csrf(newA);
        var read = await newA.PutAsJsonAsync($"/api/conversations/{conversation}/read-state", new { messageId = id });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var receipt = await b.GetStringAsync($"/api/conversations/{conversation}/receipts");
        Assert.Contains(id.ToString(), receipt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await newA.PutAsJsonAsync(
            $"/api/conversations/{conversation}/read-state", new { messageId = id })).StatusCode);
        Assert.Equal(receipt, await b.GetStringAsync($"/api/conversations/{conversation}/receipts"));
        await using var reconnected = Hub(topology.BUrl, alice.Cookie);
        await reconnected.StartAsync();
        Assert.NotEqual(oldConnectionId, reconnected.ConnectionId);
        await reconnected.InvokeAsync("Subscribe", conversation);
        var presence = await reconnected.InvokeAsync<PresencePage>("GetPresence", conversation, 0, 10);
        Assert.Contains(presence.Items, x => x.UserId == alice.Id && x.IsOnline);
    }

    [Fact]
    public async Task RedisRestartDegradesRealtimeButPreservesPostgresAndRecovers()
    {
        var isolated = new MultiInstanceTopology();
        await isolated.InitializeAsync();
        try
        {
        using var a = Client(isolated.AUrl);
        using var b = Client(isolated.BUrl);
        var alice = await Login(a);
        b.DefaultRequestHeaders.Add("Cookie", alice.Cookie);
        var response = await a.PostAsJsonAsync("/api/conversations/group",
            new { name = "Redis restart", memberIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var conversation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await isolated.Redis.StopAsync();
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await a.GetAsync("/health/realtime")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/health/ready")).StatusCode);
            var sent = await a.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
                new { body = "durable during redis restart", clientMessageId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
            var id = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Assert.Contains(id.ToString(), await b.GetStringAsync($"/api/conversations/{conversation}/messages"),
                StringComparison.OrdinalIgnoreCase);
        }
        finally { await isolated.Redis.StartAsync(); }
        await isolated.RefreshHostsIfRedisEndpointChangedAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var restored = Client(isolated.BUrl);
        while ((await restored.GetAsync("/health/realtime", timeout.Token)).StatusCode != HttpStatusCode.OK)
            await Task.Delay(100, timeout.Token);
        await using var hub = Hub(isolated.BUrl, alice.Cookie);
        await hub.StartAsync(timeout.Token);
        await hub.InvokeAsync("Subscribe", conversation, timeout.Token);
        }
        finally { await isolated.DisposeAsync(); }
    }

    [Fact]
    public async Task ConversationLockContentionEmitsWaitAndHoldSamplesWithoutTimingGate()
    {
        using var a = Client(topology.AUrl);
        var alice = await Login(a);
        using var b = Client(topology.BUrl);
        var bob = await Login(b);
        var response = await a.PostAsJsonAsync("/api/conversations/group",
            new { name = "Lock measurement", memberIds = new[] { bob.Id } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var conversation = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var samples = new ConcurrentQueue<(string Name, string Operation, double Ms)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == "Chat.Api.ConversationLock") active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var operation = tags.ToArray().Single(x => x.Key == "operation").Value?.ToString() ?? "";
            samples.Enqueue((instrument.Name, operation, value));
        });
        listener.Start();
        await using var hub = Hub(topology.AUrl, alice.Cookie);
        await hub.StartAsync();
        await hub.InvokeAsync("Subscribe", conversation);
        var sent = await UnderConversationLock(conversation, () => a.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { body = "contended", clientMessageId = Guid.NewGuid() }));
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var messageId = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await UnderConversationLock(conversation, () => a.PutAsJsonAsync(
            $"/api/conversations/{conversation}/read-state", new { messageId }))).StatusCode);
        Assert.True(await UnderConversationLock(conversation, async () =>
        {
            await hub.InvokeAsync("SetTyping", conversation, true);
            return true;
        }));
        Assert.Equal(HttpStatusCode.NoContent, (await UnderConversationLock(conversation, () =>
            a.DeleteAsync($"/api/conversations/{conversation}/members/{bob.Id}"))).StatusCode);
        foreach (var operation in new[] { "message.send", "receipt.advance", "typing.change", "membership.change" })
        {
            Assert.Contains(samples, x => x.Name == "conversation.lock.wait" && x.Operation == operation && x.Ms >= 0);
            Assert.Contains(samples, x => x.Name == "conversation.lock.hold" && x.Operation == operation && x.Ms >= 0);
        }
        foreach (var sample in samples)
            output.WriteLine("{0} {1}: {2:F2} ms", sample.Name, sample.Operation, sample.Ms);
    }

    private async Task<T> UnderConversationLock<T>(Guid conversation, Func<Task<T>> action)
    {
        await using var pg = new NpgsqlConnection(topology.Postgres.GetConnectionString());
        await pg.OpenAsync();
        await using var transaction = await pg.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT 1 FROM conversations.\"Conversations\" WHERE \"Id\" = @id FOR UPDATE", pg, transaction))
        {
            lockCommand.Parameters.AddWithValue("id", conversation);
            Assert.Equal(1, await lockCommand.ExecuteScalarAsync());
        }
        var pending = action();
        // pg_stat_activity is the barrier: the API query must be waiting on this transaction.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observer = new NpgsqlConnection(topology.Postgres.GetConnectionString());
        await observer.OpenAsync(timeout.Token);
        while (true)
        {
            await using var probe = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FOR UPDATE%'", observer);
            if ((long)(await probe.ExecuteScalarAsync(timeout.Token))! > 0) break;
            await Task.Delay(25, timeout.Token);
        }
        await transaction.CommitAsync();
        return await pending;
    }
}
