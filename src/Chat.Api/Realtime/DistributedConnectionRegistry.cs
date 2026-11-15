using Chat.Api.Infrastructure.Redis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Chat.Api.Realtime;

// Redis holds bounded, disposable routing hints. PostgreSQL remains the membership authority.
public sealed class DistributedConnectionRegistry(RedisConnection connection, RedisNamespace names,
    IOptions<RedisOptions> options, IConfiguration configuration, ILogger<DistributedConnectionRegistry> logger) : BackgroundService
{
    private sealed record LocalConnection(Guid UserId, DateTimeOffset? TicketExpiry, Action Abort);
    private readonly Dictionary<string, LocalConnection> local = new();
    private readonly object sync = new();
    public string BootEpoch { get; } = Guid.NewGuid().ToString("N");
    public string InstanceId { get; } = RedisOptions.ValidSegment(configuration["RealtimeInstance:InstanceId"]!)
        ? configuration["RealtimeInstance:InstanceId"]! : Guid.NewGuid().ToString("N");
    private string? coordinationEpoch;
    private const int MaxConnectionsPerUser = 128;
    private const int MaxSubscriptionsPerConnection = 256;
    public bool Enabled => options.Value.Enabled && options.Value.BackplaneEnabled;
    private string Key(string suffix) => names.CoordinationPrefix + suffix;
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(15);
    private TimeSpan Timeout => TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMilliseconds);

    private const string RegisterScript = """
        local now = redis.call('TIME')
        local ms = now[1] * 1000 + math.floor(now[2] / 1000)
        local deadline = math.min(ms + 15000, tonumber(ARGV[4]))
        if deadline <= ms then return {-1, 0, ''} end
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ms)
        local before = redis.call('ZCARD', KEYS[2])
        if before >= 128 then return {-2, 0, ''} end
        local existing = redis.call('HGET', KEYS[1], 'owner')
        if existing and existing ~= ARGV[2] then return {-3, 0, ''} end
        redis.call('SET', KEYS[4], ARGV[6], 'NX', 'PX', 3600000)
        redis.call('PEXPIRE', KEYS[4], 3600000)
        redis.call('HSET', KEYS[1], 'user', ARGV[1], 'owner', ARGV[2], 'epoch', ARGV[3], 'ticket', ARGV[4])
        redis.call('PEXPIREAT', KEYS[1], deadline)
        redis.call('ZADD', KEYS[2], deadline, ARGV[5])
        redis.call('PEXPIRE', KEYS[2], 3600000)
        if before == 0 then
            local revision = redis.call('INCR', KEYS[3])
            redis.call('PEXPIRE', KEYS[3], 3600000)
            return {2, revision, redis.call('GET', KEYS[4])}
        end
        return {1, 0, redis.call('GET', KEYS[4])}
        """;

    private const string RefreshScript = """
        if redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] or redis.call('HGET', KEYS[1], 'epoch') ~= ARGV[2] then return 0 end
        local now = redis.call('TIME')
        local ms = now[1] * 1000 + math.floor(now[2] / 1000)
        local deadline = math.min(ms + 15000, tonumber(ARGV[3]))
        if deadline <= ms then return 0 end
        redis.call('PEXPIREAT', KEYS[1], deadline)
        redis.call('ZADD', KEYS[2], deadline, ARGV[4])
        redis.call('PEXPIRE', KEYS[2], 3600000)
        redis.call('PEXPIRE', KEYS[3], 3600000)
        redis.call('PEXPIRE', KEYS[4], 3600000)
        return 1
        """;

    private const string RemoveScript = """
        if redis.call('HGET', KEYS[1], 'owner') ~= ARGV[1] or redis.call('HGET', KEYS[1], 'epoch') ~= ARGV[2] then return {0, 0, ''} end
        redis.call('DEL', KEYS[1], KEYS[3])
        redis.call('ZREM', KEYS[2], ARGV[3])
        local now = redis.call('TIME')
        local ms = now[1] * 1000 + math.floor(now[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ms)
        if redis.call('ZCARD', KEYS[2]) == 0 then
            local revision = redis.call('INCR', KEYS[4])
            redis.call('PEXPIRE', KEYS[4], 3600000)
            return {2, revision, redis.call('GET', KEYS[5]) or ''}
        end
        return {1, 0, redis.call('GET', KEYS[5]) or ''}
        """;

    private async Task<IDatabase> Database(CancellationToken token)
    {
        var redis = await connection.GetAsync(token);
        var db = redis.GetDatabase();
        var epoch = await db.StringGetAsync(Key("epoch")).WaitAsync(Timeout, token);
        if (epoch.IsNull)
        {
            await db.StringSetAsync(Key("epoch"), Guid.NewGuid().ToString("N"), when: When.NotExists).WaitAsync(Timeout, token);
            epoch = await db.StringGetAsync(Key("epoch")).WaitAsync(Timeout, token);
        }
        var current = (string?)epoch;
        if (current != coordinationEpoch)
        {
            // A Redis reset invalidates all old owner/subscription hints. Old sockets must reconnect.
            if (coordinationEpoch is not null)
            {
                LocalConnection[] stale;
                lock (sync) { stale = local.Values.ToArray(); local.Clear(); }
                foreach (var item in stale) item.Abort();
            }
            coordinationEpoch = current;
        }
        return db;
    }

    private RedisKey ConnectionKey(string id) => Key($"connection:{id}");
    private RedisKey UserKey(Guid id) => Key($"user:{id:N}");
    private RedisKey SubscriptionKey(string id) => Key($"subscriptions:{id}");
    private RedisKey RevisionKey(Guid user) => Key($"revision:{user:N}");
    private RedisKey LifetimeKey(Guid user) => Key($"lifetime:{user:N}");
    private static long Ticket(DateTimeOffset? expiry) => (expiry ?? DateTimeOffset.UtcNow.AddDays(1)).ToUnixTimeMilliseconds();

    public async Task<(bool Success, PresenceChange? Change)> Register(string id, Guid user, DateTimeOffset? expiry, Action abort, CancellationToken token)
    {
        if (!Enabled) return (true, null);
        var db = await Database(token);
        var result = (RedisResult[]?)await db.ScriptEvaluateAsync(RegisterScript,
            [ConnectionKey(id), UserKey(user), RevisionKey(user), LifetimeKey(user)],
            [user.ToString("N"), $"{InstanceId}:{BootEpoch}", coordinationEpoch!, Ticket(expiry), id, Guid.NewGuid().ToString("N")]).WaitAsync(Timeout, token)
            ?? throw new InvalidOperationException("Redis registration returned no result.");
        if ((int)result[0] < 1) return (false, null);
        lock (sync) local[id] = new(user, expiry, abort);
        return (true, (int)result[0] == 2 ? new(user, true, (long)result[1], (string?)result[2], coordinationEpoch) : null);
    }

    public async Task<PresenceChange?> Remove(string id, Guid user, CancellationToken token = default)
    {
        if (!Enabled) return null;
        lock (sync) local.Remove(id);
        try
        {
            var db = await Database(token);
            var result = (RedisResult[]?)await db.ScriptEvaluateAsync(RemoveScript,
                [ConnectionKey(id), UserKey(user), SubscriptionKey(id), RevisionKey(user), LifetimeKey(user)],
                [$"{InstanceId}:{BootEpoch}", coordinationEpoch!, id]).WaitAsync(Timeout, token)
                ?? throw new InvalidOperationException("Redis removal returned no result.");
            return (int)result[0] == 2 ? new(user, false, (long)result[1], (string?)result[2], coordinationEpoch) : null;
        }
        catch (Exception e) when (e is RedisException or TimeoutException or OperationCanceledException)
        { logger.LogWarning("Redis connection cleanup unavailable; lease expiry will remove presence."); return null; }
    }

    public async Task Subscribe(string id, Guid conversation, Guid membershipId, long generation, CancellationToken token)
    {
        if (!Enabled) return;
        var db = await Database(token);
        if (!await db.KeyExistsAsync(ConnectionKey(id)).WaitAsync(Timeout, token)) throw new InvalidOperationException("Connection lease expired.");
        var key = SubscriptionKey(id);
        if (await db.HashLengthAsync(key).WaitAsync(Timeout, token) >= MaxSubscriptionsPerConnection
            && !await db.HashExistsAsync(key, conversation.ToString("N")).WaitAsync(Timeout, token))
            throw new InvalidOperationException("Subscription limit reached.");
        await db.HashSetAsync(key, conversation.ToString("N"), $"{membershipId:N}:{generation}:{InstanceId}:{BootEpoch}").WaitAsync(Timeout, token);
        await db.KeyExpireAsync(key, Lease).WaitAsync(Timeout, token);
    }

    public async Task Unsubscribe(string id, Guid conversation, CancellationToken token)
    {
        if (!Enabled) return;
        var db = await Database(token);
        await db.HashDeleteAsync(SubscriptionKey(id), conversation.ToString("N")).WaitAsync(Timeout, token);
    }

    public async Task<(string Epoch, IReadOnlyDictionary<Guid, PresenceMember> Members)> Snapshot(IReadOnlyList<Guid> users, CancellationToken token)
    {
        if (!Enabled) throw new InvalidOperationException("Distributed presence is disabled.");
        var db = await Database(token);
        var result = new Dictionary<Guid, PresenceMember>();
        if (users.Count == 0) return (coordinationEpoch!, result);
        if (users.Count > 100) throw new ArgumentOutOfRangeException(nameof(users));
        var keys = users.SelectMany(user => new[] { UserKey(user), RevisionKey(user), LifetimeKey(user) }).ToArray();
        // One bounded atomic page read under the PostgreSQL roster lock.
        var state = (RedisResult[]?)await db.ScriptEvaluateAsync("""
            local now = redis.call('TIME')
            local ms = now[1] * 1000 + math.floor(now[2] / 1000)
            local result = {}
            for i = 1, #KEYS, 3 do
                redis.call('ZREMRANGEBYSCORE', KEYS[i], '-inf', ms)
                local ids = redis.call('ZRANGE', KEYS[i], 0, 127)
                local online = 0
                for _, id in ipairs(ids) do
                    if redis.call('EXISTS', ARGV[1] .. id) == 1 then online = 1; break end
                    redis.call('ZREM', KEYS[i], id)
                end
                table.insert(result, online)
                table.insert(result, redis.call('GET', KEYS[i+1]) or '0')
                table.insert(result, redis.call('GET', KEYS[i+2]) or '')
            end
            return result
            """, keys, [Key("connection:")]).WaitAsync(Timeout, token)
            ?? throw new InvalidOperationException("Redis presence returned no result.");
        for (var i = 0; i < users.Count; i++)
            result[users[i]] = new(users[i], (int)state[i * 3] == 1, (long)state[i * 3 + 1], (string?)state[i * 3 + 2]);
        return (coordinationEpoch!, result);
    }

    public async Task<IReadOnlyList<string>> Subscribers(Guid conversation, IReadOnlyDictionary<Guid, Guid> members, CancellationToken token)
    {
        var db = await Database(token);
        var recipients = new List<string>();
        foreach (var member in members)
        {
            var ids = await db.SortedSetRangeByRankAsync(UserKey(member.Key), 0, MaxConnectionsPerUser - 1).WaitAsync(Timeout, token);
            foreach (var id in ids)
            {
                var name = (string)id!;
                if (!await db.KeyExistsAsync(ConnectionKey(name)).WaitAsync(Timeout, token)) continue;
                var subscription = await db.HashGetAsync(SubscriptionKey(name), conversation.ToString("N")).WaitAsync(Timeout, token);
                if (!subscription.IsNull && ((string)subscription!).StartsWith($"{member.Value:N}:", StringComparison.Ordinal)) recipients.Add(name);
            }
        }
        return recipients;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            KeyValuePair<string, LocalConnection>[] items;
            lock (sync) items = local.ToArray();
            foreach (var item in items)
            {
                try
                {
                    var db = await Database(stoppingToken);
                    var ok = (int)await db.ScriptEvaluateAsync(RefreshScript,
                        [ConnectionKey(item.Key), UserKey(item.Value.UserId), RevisionKey(item.Value.UserId), LifetimeKey(item.Value.UserId)],
                        [$"{InstanceId}:{BootEpoch}", coordinationEpoch!, Ticket(item.Value.TicketExpiry), item.Key]).WaitAsync(Timeout, stoppingToken);
                    if (ok != 1) { item.Value.Abort(); lock (sync) local.Remove(item.Key); }
                    else await db.KeyExpireAsync(SubscriptionKey(item.Key), Lease).WaitAsync(Timeout, stoppingToken);
                }
                catch (Exception e) when (e is RedisException or TimeoutException)
                {
                    // Unknown registry state must not be presented as healthy local presence.
                    item.Value.Abort(); lock (sync) local.Remove(item.Key);
                    logger.LogWarning("Redis heartbeat failed; realtime connection aborted for recovery.");
                }
            }
        }
    }
}
