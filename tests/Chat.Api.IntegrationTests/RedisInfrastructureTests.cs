using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Infrastructure;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Infrastructure.Redis;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Chat.Api.IntegrationTests;

public sealed class RedisInfrastructureTests(MultiInstanceTopology topology) : IClassFixture<MultiInstanceTopology>
{
    [Fact]
    public async Task TwoTcpHostsShareDatabaseRedisCookiesAndCsrfWithExplicitRouting()
    {
        Assert.NotEqual(topology.AUrl, topology.BUrl);
        Assert.Equal("instance-a", topology.A.Services.GetRequiredService<IOptions<RealtimeInstanceOptions>>().Value.InstanceId);
        Assert.Equal("instance-b", topology.B.Services.GetRequiredService<IOptions<RealtimeInstanceOptions>>().Value.InstanceId);
        var cookies = new CookieContainer();
        using var a = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = topology.AUrl };
        using var b = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = topology.BUrl };
        foreach (var client in new[] { a, b })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/realtime")).StatusCode);
        }
        var id = Guid.NewGuid().ToString("N");
        var email = $"{id}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync("/api/auth/register",
            new { email, userName = id, password = "Test-password123!" })).StatusCode);
        var csrf = await a.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        b.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await b.PostAsJsonAsync("/api/auth/login",
            new { email, password = "Test-password123!" })).StatusCode);
        Assert.Equal(email, (await a.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("email").GetString());
        Assert.Equal(email, (await b.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("email").GetString());
        using var scope = topology.B.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.True(await db.Users.AnyAsync(user => user.Email == email));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var ra = await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync();
        var rb = await topology.B.Services.GetRequiredService<RedisConnection>().GetAsync();
        var key = topology.A.Services.GetRequiredService<RedisNamespace>().CoordinationPrefix + "test-probe";
        await ra.GetDatabase().StringSetAsync(key, id, TimeSpan.FromSeconds(30));
        Assert.Equal(id, (string?)await rb.GetDatabase().StringGetAsync(key));
        await ra.GetDatabase().KeyDeleteAsync(key);
    }

    [Fact]
    public async Task UniqueRunNamespacesIsolateKeysAndLiteralPubSubChannelsOnSameRedis()
    {
        await using var other = topology.CreateInstance("other", Guid.NewGuid().ToString("N"));
        using var client = other.CreateClient();
        var one = topology.A.Services.GetRequiredService<RedisNamespace>();
        var two = other.Services.GetRequiredService<RedisNamespace>();
        var first = await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync();
        var second = await other.Services.GetRequiredService<RedisConnection>().GetAsync();
        Assert.Equal(first.GetDatabase().Database, second.GetDatabase().Database);
        Assert.NotEqual(one.CoordinationPrefix, two.CoordinationPrefix);
        Assert.NotEqual(one.CoordinationPrefix, one.BackplanePrefix);
        await first.GetDatabase().StringSetAsync(one.CoordinationPrefix + "probe", "first", TimeSpan.FromSeconds(30));
        Assert.True((await second.GetDatabase().StringGetAsync(two.CoordinationPrefix + "probe")).IsNull);
        var channel1 = RedisChannel.Literal(one.BackplanePrefix + "probe");
        var channel2 = RedisChannel.Literal(two.BackplanePrefix + "probe");
        var queue1 = await first.GetSubscriber().SubscribeAsync(channel1);
        var queue2 = await second.GetSubscriber().SubscribeAsync(channel2);
        try
        {
            // Subscriber counts prove namespace isolation; this is not SignalR routing.
            Assert.Equal(1, await first.GetSubscriber().PublishAsync(channel1, "one"));
            Assert.Equal(1, await first.GetSubscriber().PublishAsync(channel2, "two"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal("one", (string?) (await queue1.ReadAsync(timeout.Token)).Message);
            Assert.Equal("two", (string?) (await queue2.ReadAsync(timeout.Token)).Message);
        }
        finally
        {
            await queue1.UnsubscribeAsync();
            await queue2.UnsubscribeAsync();
            await first.GetDatabase().KeyDeleteAsync(one.CoordinationPrefix + "probe");
        }
    }

    [Fact]
    public async Task RedisOutageIsObservableWhileDurableReadinessAndWritesContinue()
    {
        using var a = topology.A.CreateClient();
        using var b = topology.B.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/health/realtime")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/health/realtime")).StatusCode);
        await using var offline = topology.CreateInstance("offline-start");
        await topology.Redis.PauseAsync();
        try
        {
            foreach (var client in new[] { a, b })
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/realtime")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            }
            // Also prove a new API host starts without Redis connectivity.
            using var clientOffline = offline.CreateClient();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await clientOffline.GetAsync("/health/realtime")).StatusCode);
            var id = Guid.NewGuid().ToString("N");
            Assert.Equal(HttpStatusCode.Created, (await clientOffline.PostAsJsonAsync("/api/auth/register",
                new { email = $"{id}@example.test", userName = id, password = "Test-password123!" })).StatusCode);
            using var scope = topology.B.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Users.AnyAsync(user => user.UserName == id));
        }
        finally { await topology.Redis.UnpauseAsync(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await a.GetAsync("/health/realtime", deadline.Token)).StatusCode != HttpStatusCode.OK)
            await Task.Delay(100, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/health/realtime")).StatusCode);
    }

    [Fact]
    public async Task DisabledRedisDoesNotChangeExistingLivenessAndReportsNotReadyForRealtime()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/realtime")).StatusCode);
    }

    [Theory]
    [InlineData("deployment:collision")]
    [InlineData("wildcard*")]
    [InlineData("")]
    public void InvalidNamespaceIsRejected(string deployment)
    {
        Assert.False(new RedisOptions { Enabled = true, ConnectionString = "localhost:6379", Deployment = deployment }.IsValid());
    }
}
