using Chat.Api.Infrastructure.Redis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Chat.Api.Realtime;

// Redis holds bounded, disposable routing hints. PostgreSQL remains the membership authority.
public sealed class DistributedConnectionRegistry(RedisConnection connection, RedisNamespace names,
    IOptions<RedisOptions> options, IConfiguration configuration, ILogger<DistributedConnectionRegistry> logger) : BackgroundService
{
    public sealed record RevocationConnection(string Id, string Owner, string Subscription);
    public sealed record RevocationSnapshot(string Epoch, IReadOnlyList<string> Owners,
        IReadOnlyList<RevocationConnection> Connections);
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
    public string Owner => $"{InstanceId}:{BootEpoch}";
    public async Task<string> RequireEpoch(CancellationToken token)
    {
        await Database(token);
        return coordinationEpoch!;
    }
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
    private RedisKey OwnerKey(string owner) => Key($"owner:{owner}");
    private RedisKey OwnersKey => Key("owners");
    private static long Ticket(DateTimeOffset? expiry) => (expiry ?? DateTimeOffset.UtcNow.AddDays(1)).ToUnixTimeMilliseconds();

    private async Task TouchOwner(IDatabase db, CancellationToken token)
    {
        var accepted = (int)await db.ScriptEvaluateAsync("""
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
            local time = redis.call('TIME')
            local now = time[1] * 1000 + math.floor(time[2] / 1000)
            redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
            if redis.call('ZCARD', KEYS[2]) >= 256 and redis.call('ZSCORE', KEYS[2], ARGV[2]) == false then return -1 end
            redis.call('SET', KEYS[3], ARGV[1], 'PX', 15000)
            redis.call('ZADD', KEYS[2], now + 15000, ARGV[2])
            redis.call('PEXPIRE', KEYS[2], 3600000)
            return 1
            """, [Key("epoch"), OwnersKey, OwnerKey(Owner)], [coordinationEpoch!, Owner])
            .WaitAsync(Timeout, token);
        if (accepted != 1) throw new InvalidOperationException("Redis owner registration unavailable.");
    }

    // Called while the caller holds the PostgreSQL conversation lock. A selected owner remains
    // required even if its lease expires after this snapshot.
    public async Task<RevocationSnapshot> SnapshotRevocation(Guid conversation, Guid user, Guid membership,
        CancellationToken token)
    {
        var db = await Database(token);
        await TouchOwner(db, token);
        var epoch = coordinationEpoch!;
        var owners = (RedisResult[]?)await db.ScriptEvaluateAsync("""
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return redis.error_reply('epoch changed') end
            local time = redis.call('TIME')
            local now = time[1] * 1000 + math.floor(time[2] / 1000)
            redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
            local result = {}
            for _, owner in ipairs(redis.call('ZRANGE', KEYS[2], 0, 255)) do
                if redis.call('GET', ARGV[2] .. owner) == ARGV[1] then table.insert(result, owner)
                else redis.call('ZREM', KEYS[2], owner) end
            end
            return result
            """, [Key("epoch"), OwnersKey], [epoch, Key("owner:")]).WaitAsync(Timeout, token)
            ?? throw new InvalidOperationException("Owner snapshot unavailable.");
        var required = owners.Select(x => (string)x!).ToArray();
        if (!required.Contains(Owner)) throw new InvalidOperationException("Local owner not registered.");
        var connections = new List<RevocationConnection>();
        var ids = await db.SortedSetRangeByRankAsync(UserKey(user), 0, MaxConnectionsPerUser - 1)
            .WaitAsync(Timeout, token);
        foreach (var id in ids)
        {
            var name = (string)id!;
            var state = await db.HashGetAsync(ConnectionKey(name), ["owner", "epoch", "user"])
                .WaitAsync(Timeout, token);
            if (state[0].IsNull) continue;
            if ((string?)state[1] != epoch || (string?)state[2] != user.ToString("N"))
                throw new InvalidOperationException("Connection registry uncertain.");
            var subscription = await db.HashGetAsync(SubscriptionKey(name), conversation.ToString("N"))
                .WaitAsync(Timeout, token);
            if (!subscription.IsNull && ((string)subscription!).StartsWith($"{membership:N}:", StringComparison.Ordinal))
            {
                var owner = (string)state[0]!;
                if (!required.Contains(owner) || !((string)subscription!).EndsWith($":{owner}", StringComparison.Ordinal))
                    throw new InvalidOperationException("Subscription owner uncertain.");
                connections.Add(new(name, owner, (string)subscription!));
            }
        }
        if (await RequireEpoch(token) != epoch) throw new InvalidOperationException("Coordination epoch changed.");
        return new(epoch, required, connections);
    }

    public async Task<(bool Success, PresenceChange? Change)> Register(string id, Guid user, DateTimeOffset? expiry, Action abort, CancellationToken token)
    {
        if (!Enabled) return (true, null);
        var db = await Database(token);
        await TouchOwner(db, token);
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

    public async Task<bool> UnsubscribeExact(string id, Guid conversation, string expected,
        string epoch, CancellationToken token)
    {
        var db = await Database(token);
        return (int)await db.ScriptEvaluateAsync("""
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return -1 end
            if redis.call('HGET', KEYS[2], ARGV[2]) ~= ARGV[3] then return 0 end
            redis.call('HDEL', KEYS[2], ARGV[2])
            return 1
            """, [Key("epoch"), SubscriptionKey(id)], [epoch, conversation.ToString("N"), expected])
            .WaitAsync(Timeout, token) == 1;
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
            var ids = (RedisResult[]?)await db.ScriptEvaluateAsync("""
                if redis.call('GET', KEYS[1]) ~= ARGV[1] then return redis.error_reply('coordination epoch changed') end
                local result = {}
                local now = redis.call('TIME')
                local ms = now[1] * 1000 + math.floor(now[2] / 1000)
                redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ms)
                for _, id in ipairs(redis.call('ZRANGE', KEYS[2], 0, 127)) do
                    local conn = ARGV[5] .. id
                    local sub = redis.call('HGET', ARGV[6] .. id, ARGV[2])
                    local owner = redis.call('HGET', conn, 'owner')
                    if redis.call('HGET', conn, 'epoch') == ARGV[1]
                        and redis.call('HGET', conn, 'user') == ARGV[3]
                        and tonumber(redis.call('HGET', conn, 'ticket') or '0') > ms
                        and owner and redis.call('GET', ARGV[7] .. owner) == ARGV[1]
                        and sub and string.sub(sub, 1, 33) == ARGV[4] .. ':'
                        and string.sub(sub, -string.len(owner) - 1) == ':' .. owner then
                        table.insert(result, id)
                    end
                end
                return result
                """, [Key("epoch"), UserKey(member.Key)],
                [coordinationEpoch!, conversation.ToString("N"), member.Key.ToString("N"), member.Value.ToString("N"),
                    Key("connection:"), Key("subscriptions:"), Key("owner:")]).WaitAsync(Timeout, token)
                ?? throw new InvalidOperationException("Redis recipient selection returned no result.");
            recipients.AddRange(ids.Select(id => (string)id!));
        }
        return recipients;
    }

    public async Task<IReadOnlyList<string>> ConnectionsForUser(Guid user, CancellationToken token)
    {
        var db = await Database(token);
        var result = (RedisResult[]?)await db.ScriptEvaluateAsync("""
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return redis.error_reply('coordination epoch changed') end
            local time = redis.call('TIME')
            local now = time[1] * 1000 + math.floor(time[2] / 1000)
            redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
            local result = {}
            for _, id in ipairs(redis.call('ZRANGE', KEYS[2], 0, 127)) do
                local connection = ARGV[3] .. id
                local owner = redis.call('HGET', connection, 'owner')
                if owner and redis.call('GET', ARGV[4] .. owner) == ARGV[1]
                    and redis.call('HGET', connection, 'epoch') == ARGV[1]
                    and redis.call('HGET', connection, 'user') == ARGV[2]
                    and tonumber(redis.call('HGET', connection, 'ticket') or '0') > now then
                    table.insert(result, id)
                end
            end
            return result
            """, [Key("epoch"), UserKey(user)],
            [coordinationEpoch!, user.ToString("N"), Key("connection:"), Key("owner:")])
            .WaitAsync(Timeout, token) ?? throw new InvalidOperationException("Redis user routing unavailable.");
        return result.Select(x => (string)x!).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try { await TouchOwner(await Database(stoppingToken), stoppingToken); }
        catch (Exception error) when (error is RedisException or TimeoutException or InvalidOperationException)
        { logger.LogWarning(error, "Initial owner registration unavailable."); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TouchOwner(await Database(stoppingToken), stoppingToken); }
            catch (Exception error) when (error is RedisException or TimeoutException or InvalidOperationException)
            { logger.LogWarning(error, "Owner heartbeat unavailable."); }
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopping = base.StopAsync(cancellationToken);
        try
        {
            if (!Enabled || coordinationEpoch is null) return;
            KeyValuePair<string, LocalConnection>[] remaining;
            lock (sync) remaining = local.ToArray();
            foreach (var item in remaining)
            {
                item.Value.Abort();
                try { await Remove(item.Key, item.Value.UserId, CancellationToken.None); }
                catch (Exception error) when (error is ObjectDisposedException or AggregateException or RedisException or TimeoutException)
                { /* Lease expiry fences an unavailable coordination connection during shutdown. */ }
            }
            try
            {
                var db = (await connection.GetAsync(CancellationToken.None)).GetDatabase();
                await db.ScriptEvaluateAsync("""
                    if redis.call('GET', KEYS[1]) == ARGV[1]
                        and redis.call('GET', KEYS[2]) == ARGV[1] then
                        redis.call('DEL', KEYS[2])
                        redis.call('ZREM', KEYS[3], ARGV[2])
                    end
                    return 1
                    """, [Key("epoch"), OwnerKey(Owner), OwnersKey], [coordinationEpoch, Owner])
                    .WaitAsync(Timeout, CancellationToken.None);
            }
            catch (Exception error) when (error is RedisException or TimeoutException or OperationCanceledException or ObjectDisposedException)
            { /* Avoid logging through a provider that may already be disposed during host shutdown. */ }
        }
        finally { await stopping; }
    }
}
