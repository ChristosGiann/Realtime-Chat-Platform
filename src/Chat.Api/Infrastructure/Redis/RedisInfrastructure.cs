using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Chat.Api.Infrastructure.Redis;

public sealed class RedisOptions
{
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = "";
    public string ApplicationName { get; set; } = "realtime-chat";
    public string Deployment { get; set; } = "";
    public string ProtocolVersion { get; set; } = "v1";
    public string RunId { get; set; } = "shared";
    public int OperationTimeoutMilliseconds { get; set; } = 2000;

    public static bool ValidSegment(string value) => value is not null &&
        Regex.IsMatch(value, @"\A[a-zA-Z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
    public bool IsValid()
    {
        if (!Enabled) return true;
        if (!new[] { ApplicationName, Deployment, ProtocolVersion, RunId }.All(ValidSegment)
            || OperationTimeoutMilliseconds is < 100 or > 10000) return false;
        try { return !string.IsNullOrWhiteSpace(ConnectionString) && ConfigurationOptions.Parse(ConnectionString).EndPoints.Count > 0; }
        catch (ArgumentException) { return false; }
    }
}

// A namespace builder, not a registry or an authorization cache.
public sealed class RedisNamespace(IOptions<RedisOptions> options)
{
    private readonly RedisOptions settings = options.Value;
    public string Prefix => $"{settings.ApplicationName}:{settings.Deployment}:{settings.ProtocolVersion}:{settings.RunId}";
    public string CoordinationPrefix => $"{Prefix}:coordination:";
    public string BackplanePrefix => $"{Prefix}:backplane:";
}

// One lazy multiplexer per API host. A Redis outage never blocks application startup.
public sealed class RedisConnection(IOptions<RedisOptions> options) : IAsyncDisposable
{
    private readonly RedisOptions settings = options.Value;
    private readonly Lazy<Task<ConnectionMultiplexer>> connection = new(() =>
    {
        var settings = options.Value;
        var config = ConfigurationOptions.Parse(settings.ConnectionString);
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = settings.OperationTimeoutMilliseconds;
        config.AsyncTimeout = settings.OperationTimeoutMilliseconds;
        config.SyncTimeout = settings.OperationTimeoutMilliseconds;
        config.ConnectRetry = 0;
        config.BacklogPolicy = BacklogPolicy.FailFast;
        return ConnectionMultiplexer.ConnectAsync(config);
    });

    public async Task<IConnectionMultiplexer> GetAsync(CancellationToken token = default)
    {
        if (!settings.Enabled) throw new InvalidOperationException("Redis infrastructure is disabled.");
        return await connection.Value.WaitAsync(TimeSpan.FromMilliseconds(settings.OperationTimeoutMilliseconds), token);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection.IsValueCreated)
        {
            try { (await connection.Value).Dispose(); }
            catch { /* A failed connection task owns no usable multiplexer. */ }
        }
    }
}

public sealed class RedisConnectivityHealthCheck(RedisConnection connection, IOptions<RedisOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return HealthCheckResult.Degraded("Redis infrastructure disabled.");
        try
        {
            var redis = await connection.GetAsync(cancellationToken);
            await redis.GetDatabase().PingAsync().WaitAsync(
                TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMilliseconds), cancellationToken);
            return HealthCheckResult.Healthy("Redis connectivity available; distributed realtime is not enabled.");
        }
        catch (Exception error) when (error is RedisException or TimeoutException or OperationCanceledException)
        {
            // Do not expose connection strings/endpoints/credentials in health responses or logs.
            return HealthCheckResult.Unhealthy("Redis connectivity unavailable.");
        }
    }
}

public static class RedisInfrastructure
{
    public static IServiceCollection AddRedisInfrastructure(this IServiceCollection services)
    {
        services.AddOptions<RedisOptions>().BindConfiguration("Redis")
            .Validate(value => value.IsValid(), "Redis configuration is invalid; check namespace, connection and timeout settings.")
            .ValidateOnStart();
        services.AddSingleton<RedisNamespace>();
        services.AddSingleton<RedisConnection>();
        services.AddHealthChecks().AddCheck<RedisConnectivityHealthCheck>("redis", tags: ["realtime"]);
        return services;
    }
}
