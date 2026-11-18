using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Chat.Api.IntegrationTests;

// Real TCP Kestrel hosts; callers choose A/B explicitly. Ephemeral ports avoid parallel-run collisions.
public sealed class MultiInstanceTopology : IAsyncLifetime
{
    public const string RedisImage = "redis:8.4.8-alpine3.22";
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:18").Build();
    public RedisContainer Redis { get; } = new RedisBuilder(RedisImage).Build();
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public bool FailBGroupRemoval { get; set; }
    public WebApplicationFactory<Program> A { get; private set; } = null!;
    public WebApplicationFactory<Program> B { get; private set; } = null!;
    public Uri AUrl { get; private set; } = null!;
    public Uri BUrl { get; private set; } = null!;
    private string redisConnectionString = "";
    private string KeyPath => Path.Combine(Path.GetTempPath(), "chat-stage6-tests", RunId);

    public WebApplicationFactory<Program> CreateInstance(string identity, string? runId = null, bool backplane = false,
        bool failGroupRemoval = false)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development")
                .UseSetting("Redis:BackplaneEnabled", backplane.ToString())
                .UseSetting("Redis:Enabled", "true")
                .UseSetting("Redis:ConnectionString", redisConnectionString)
                .UseSetting("Redis:ApplicationName", "chat-tests")
                .UseSetting("Redis:Deployment", "integration")
                .UseSetting("Redis:ProtocolVersion", "v1")
                .UseSetting("Redis:RunId", runId ?? RunId)
                .ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ChatDatabase"] = Postgres.GetConnectionString(),
                    ["Redis:Enabled"] = "true",
                    ["Redis:BackplaneEnabled"] = backplane.ToString(),
                    ["Redis:ConnectionString"] = redisConnectionString,
                    ["Redis:ApplicationName"] = "chat-tests",
                    ["Redis:Deployment"] = "integration",
                    ["Redis:ProtocolVersion"] = "v1",
                    ["Redis:RunId"] = runId ?? RunId,
                    ["RealtimeInstance:InstanceId"] = identity,
                    ["SharedDataProtection:KeyRingPath"] = KeyPath,
                    ["SharedDataProtection:ApplicationName"] = $"chat-tests-{RunId}"
                }))
                .ConfigureServices(services =>
                {
                    if (failGroupRemoval) services.AddSingleton<IGroupManager, FailingRemovalGroups>();
                }));
        factory.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        return factory;
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync());
            redisConnectionString = Redis.GetConnectionString();
            A = CreateInstance("instance-a", backplane: true);
            using var a = A.CreateClient();
            AUrl = a.BaseAddress!;
            using (var scope = A.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
            // Persist a shared key before starting another independent host/provider.
            A.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("topology").Protect("ready");
            B = CreateInstance("instance-b", backplane: true, failGroupRemoval: FailBGroupRemoval);
            using var b = B.CreateClient();
            BUrl = b.BaseAddress!;
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task RestartAAsync()
    {
        await A.DisposeAsync();
        A = CreateInstance("instance-a", backplane: true);
        using var client = A.CreateClient();
        AUrl = client.BaseAddress!;
    }

    public async Task RefreshHostsIfRedisEndpointChangedAsync()
    {
        var current = Redis.GetConnectionString();
        if (current == redisConnectionString) return;
        await B.DisposeAsync();
        await A.DisposeAsync();
        redisConnectionString = current;
        A = CreateInstance("instance-a", backplane: true);
        using var a = A.CreateClient();
        AUrl = a.BaseAddress!;
        B = CreateInstance("instance-b", backplane: true);
        using var b = B.CreateClient();
        BUrl = b.BaseAddress!;
    }

    public async Task DisposeAsync()
    {
        if (B is not null) await B.DisposeAsync();
        if (A is not null) await A.DisposeAsync();
        await Redis.DisposeAsync();
        await Postgres.DisposeAsync();
        // Only this fixture's generated directory, never a configured production key ring.
        if (Directory.Exists(KeyPath)) Directory.Delete(KeyPath, recursive: true);
    }

    private sealed class FailingRemovalGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated remote group cleanup failure.");
    }
}
