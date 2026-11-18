using System.Collections.Concurrent;
using Chat.Api.Infrastructure.Redis;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Chat.Api.Realtime;

// Redis stores only disposable activity leases and ordering tokens, never drafts or messages.
public sealed class DistributedTyping(DistributedConnectionRegistry registry, RedisConnection connection,
    RedisNamespace names, IOptions<RedisOptions> options, IHubContext<ChatHub> hub, ILogger<DistributedTyping> logger)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(Guid Conversation, long Generation), long>> sequences = new();
    private readonly SemaphoreSlim epochGate = new(1, 1);
    private long failureGeneration;
    private volatile bool uncertain;
    private void MarkUncertain() { Interlocked.Increment(ref failureGeneration); uncertain = true; }
    private string? knownActivityEpoch;
    public void ForgetConnection(string id) => sequences.TryRemove(id, out _);
    public void ForgetSubscription(string id, Guid conversation)
    {
        if (!sequences.TryGetValue(id, out var local)) return;
        foreach (var key in local.Keys.Where(x => x.Conversation == conversation)) local.TryRemove(key, out _);
    }
    private TimeSpan Timeout => TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMilliseconds);
    private string Key(string suffix) => names.CoordinationPrefix + suffix;
    private RedisKey Aggregate(Guid conversation, Guid user) => Key($"typing:{conversation:N}:{user:N}");
    private RedisKey Revision(Guid conversation, Guid user) => Key($"typing-revision:{conversation:N}:{user:N}");
    private RedisKey Lifetime(Guid conversation, Guid user) => Key($"typing-lifetime:{conversation:N}:{user:N}");
    private RedisKey State(Guid conversation, Guid user) => Key($"typing-state:{conversation:N}:{user:N}");
    private RedisKey Throttle(string connectionId, Guid conversation, long generation) =>
        Key($"typing-throttle:{connectionId}:{conversation:N}:{generation}");
    private RedisKey Sequence(string connectionId, Guid conversation, long generation) =>
        Key($"typing-sequence:{connectionId}:{conversation:N}:{generation}");
    private RedisKey Operation(string connectionId, Guid conversation, long generation, Guid activityId) =>
        Key($"typing-operation:{connectionId}:{conversation:N}:{generation}:{activityId:N}");

    // Every ZSET entry carries the exact subscription incarnation. Reads discard a lease as soon as
    // the parent owner or subscription disappears, even if the 5-second Redis key has not expired.
    private const string Probe = """
        local function remaining(aggregate, conversation, membership, connPrefix, subPrefix, ownerPrefix, epoch, activityEpoch, now)
            redis.call('ZREMRANGEBYSCORE', aggregate, '-inf', now)
            local entries = redis.call('ZRANGE', aggregate, 0, 255, 'WITHSCORES')
            local maximum = 0
            for i = 1, #entries, 2 do
                local value = entries[i]
                local separator = string.find(value, '|', 1, true)
                local lastSeparator = string.match(value, '^.*()|')
                local connection = separator and string.sub(value, 1, separator - 1) or ''
                local subscription = separator and lastSeparator and string.sub(value, separator + 1, lastSeparator - 1) or ''
                local leaseEpoch = lastSeparator and string.sub(value, lastSeparator + 1) or ''
                local owner = string.match(subscription, '^[^:]+:[^:]+:(.+)$') or ''
                local valid = separator and leaseEpoch == activityEpoch and string.sub(subscription, 1, 33) == membership .. ':'
                    and redis.call('HGET', connPrefix .. connection, 'owner') == owner
                    and redis.call('GET', ownerPrefix .. owner) == epoch
                    and redis.call('HGET', connPrefix .. connection, 'epoch') == epoch
                    and redis.call('HGET', subPrefix .. connection, conversation) == subscription
                if not valid then redis.call('ZREM', aggregate, value)
                else maximum = math.max(maximum, tonumber(entries[i+1]) - now) end
            end
            return math.max(0, math.min(5000, maximum))
        end
        """;

    private static readonly string ChangeScript = Probe + "\n" + """
        local nowParts = redis.call('TIME')
        local now = nowParts[1] * 1000 + math.floor(nowParts[2] / 1000)
        if redis.call('GET', KEYS[1]) ~= ARGV[1]
            or redis.call('GET', KEYS[10]) ~= ARGV[12]
            or redis.call('GET', ARGV[13] .. ARGV[2]) ~= ARGV[1]
            or redis.call('HGET', KEYS[2], 'owner') ~= ARGV[2]
            or redis.call('HGET', KEYS[2], 'epoch') ~= ARGV[1]
            or redis.call('HGET', KEYS[3], ARGV[3]) ~= ARGV[4] then return {-1, 0, 0, ''} end
        if redis.call('EXISTS', KEYS[11]) == 1 then return {0, 0, 0, ''} end
        local previousSequence = tonumber(redis.call('GET', KEYS[9]) or '-1')
        if tonumber(ARGV[7]) <= previousSequence then return {0, 0, 0, ''} end
        if ARGV[6] == '1' then
            local previous = tonumber(redis.call('GET', KEYS[8]) or '-1000000000')
            if now - previous < 1000 then return {0, 0, 0, ''} end
            redis.call('ZREMRANGEBYSCORE', KEYS[4], '-inf', now)
            remaining(KEYS[4], ARGV[3], ARGV[11], ARGV[9], ARGV[10], ARGV[13], ARGV[1], ARGV[12], now)
            if redis.call('ZCARD', KEYS[4]) >= 256 and redis.call('ZSCORE', KEYS[4], ARGV[5]) == false then
                return {-2, 0, 0, ''}
            end
            redis.call('SET', KEYS[8], now, 'PX', 5000)
            redis.call('ZADD', KEYS[4], now + 5000, ARGV[5])
            redis.call('PEXPIRE', KEYS[4], 5000)
        else
            if redis.call('ZREM', KEYS[4], ARGV[5]) == 0 then return {0, 0, 0, ''} end
        end
        redis.call('SET', KEYS[9], ARGV[7], 'PX', 30000)
        redis.call('SET', KEYS[11], '1', 'PX', 30000)
        local remainingMs = remaining(KEYS[4], ARGV[3], ARGV[11], ARGV[9], ARGV[10], ARGV[13], ARGV[1], ARGV[12], now)
        redis.call('SET', KEYS[7], remainingMs > 0 and '1' or '0', 'PX', 3600000)
        redis.call('SET', KEYS[6], ARGV[8], 'NX', 'PX', 3600000)
        redis.call('PEXPIRE', KEYS[6], 3600000)
        local revision = redis.call('INCR', KEYS[5])
        redis.call('PEXPIRE', KEYS[5], 3600000)
        return {1, remainingMs, revision, redis.call('GET', KEYS[6])}
        """;

    private static readonly string SnapshotScript = Probe + "\n" + """
        local nowParts = redis.call('TIME')
        local now = nowParts[1] * 1000 + math.floor(nowParts[2] / 1000)
        if redis.call('GET', KEYS[1]) ~= ARGV[1] or redis.call('GET', KEYS[6]) ~= ARGV[6] then return {-1, 0, ''} end
        local remainingMs = remaining(KEYS[2], ARGV[2], ARGV[3], ARGV[4], ARGV[5], ARGV[7], ARGV[1], ARGV[6], now)
        if remainingMs == 0 and redis.call('GET', KEYS[5]) == '1' then
            redis.call('SET', KEYS[5], '0', 'PX', 3600000)
            redis.call('INCR', KEYS[3])
            redis.call('PEXPIRE', KEYS[3], 3600000)
        end
        return {remainingMs, redis.call('GET', KEYS[3]) or '0', redis.call('GET', KEYS[4]) or ''}
        """;

    private async Task<IDatabase> Database(CancellationToken token) => (await connection.GetAsync(token)).GetDatabase();
    private async Task<RedisResult> Evaluate(IDatabase db, string script, RedisKey[] keys, RedisValue[] values, CancellationToken token)
    {
        try { return await db.ScriptEvaluateAsync(script, keys, values).WaitAsync(Timeout, token); }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            MarkUncertain();
            logger.LogWarning(error, "Typing coordination uncertain; previous activity will be fenced after recovery.");
            throw new HubException("Typing unavailable; retry after recovery.");
        }
    }
    private async Task<(string Coordination, string Activity, IDatabase Database)> Epochs(CancellationToken token)
    {
        await epochGate.WaitAsync(token);
        var observedFailure = Interlocked.Read(ref failureGeneration);
        try
        {
            var coordination = await registry.RequireEpoch(token);
            var db = await Database(token);
            var key = Key("typing-epoch");
            RedisValue activity;
            if (uncertain && knownActivityEpoch is not null)
            {
                // CAS rotates once across all recovering instances; a later recovery observes the winner.
                activity = (string?)await db.ScriptEvaluateAsync("""
                    if redis.call('GET', KEYS[1]) == ARGV[1] then redis.call('SET', KEYS[1], ARGV[2]) end
                    return redis.call('GET', KEYS[1])
                    """, [key], [knownActivityEpoch, Guid.NewGuid().ToString("N")]).WaitAsync(Timeout, token);
            }
            else activity = await db.StringGetAsync(key).WaitAsync(Timeout, token);
            if (activity.IsNull)
            {
                await db.StringSetAsync(key, Guid.NewGuid().ToString("N"), when: When.NotExists).WaitAsync(Timeout, token);
                activity = await db.StringGetAsync(key).WaitAsync(Timeout, token);
            }
            knownActivityEpoch = activity.ToString();
            if (observedFailure == Interlocked.Read(ref failureGeneration)) uncertain = false;
            return (coordination, knownActivityEpoch, db);
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            MarkUncertain();
            logger.LogWarning(error, "Typing coordination uncertain; previous leases will be fenced after recovery.");
            throw new HubException("Typing unavailable; retry after recovery.");
        }
        finally { epochGate.Release(); }
    }
    private static RedisResult[] Result(RedisResult? value) => (RedisResult[]?)value
        ?? throw new HubException("Typing unavailable.");

    public async Task<TypingChanged?> Change(string connectionId, Guid conversation, Guid actor, Guid membership,
        long generation, bool active, CancellationToken token, Guid? activityId = null)
    {
        var (epoch, activityEpoch, db) = await Epochs(token);
        var owner = registry.Owner;
        var subscription = $"{membership:N}:{generation}:{owner}";
        var sequence = sequences.GetOrAdd(connectionId, _ => new())
            .AddOrUpdate((conversation, generation), 1, (_, previous) => previous + 1);
        var keys = new RedisKey[] { Key("epoch"), Key($"connection:{connectionId}"), Key($"subscriptions:{connectionId}"),
            Aggregate(conversation, actor), Revision(conversation, actor), Lifetime(conversation, actor), State(conversation, actor),
            Throttle(connectionId, conversation, generation), Sequence(connectionId, conversation, generation), Key("typing-epoch"),
            Operation(connectionId, conversation, generation, activityId is { } id && id != Guid.Empty ? id : Guid.NewGuid()) };
        try
        {
            var result = Result(await Evaluate(db, ChangeScript, keys,
                [epoch, owner, conversation.ToString("N"), subscription, $"{connectionId}|{subscription}|{activityEpoch}", active ? "1" : "0",
                    sequence, Guid.NewGuid().ToString("N"), Key("connection:"), Key("subscriptions:"), membership.ToString("N"),
                    activityEpoch, Key("owner:")], token));
            if ((int)result[0] == 0) return null;
            if ((int)result[0] < 0) throw new HubException("Typing unavailable; resubscribe after recovery.");
            return new(conversation, actor, (int)result[1], (long)result[2], $"{epoch}:{activityEpoch}", (string?)result[3]);
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            MarkUncertain();
            logger.LogWarning(error, "Distributed typing update failed; no local fallback.");
            throw new HubException("Typing unavailable; retry after recovery.");
        }
    }

    public async Task<TypingSnapshot> Snapshot(Guid conversation, IReadOnlyDictionary<Guid, Guid> members,
        string actorConnection, Guid actor, long generation, CancellationToken token)
    {
        var (epoch, activityEpoch, db) = await Epochs(token);
        var expected = $"{members[actor]:N}:{generation}:{registry.Owner}";
        var valid = (int)await Evaluate(db, """
            if redis.call('GET', KEYS[1]) ~= ARGV[1]
                or redis.call('GET', ARGV[5] .. ARGV[2]) ~= ARGV[1]
                or redis.call('HGET', KEYS[2], 'owner') ~= ARGV[2]
                or redis.call('HGET', KEYS[2], 'epoch') ~= ARGV[1]
                or redis.call('HGET', KEYS[3], ARGV[3]) ~= ARGV[4] then return 0 end
            return 1
            """, [Key("epoch"), Key($"connection:{actorConnection}"), Key($"subscriptions:{actorConnection}")],
            [epoch, registry.Owner, conversation.ToString("N"), expected, Key("owner:")], token);
        if (valid != 1) throw new HubException("Typing unavailable; resubscribe after recovery.");
        var items = new List<TypingMember>();
        foreach (var member in members)
        {
            var result = Result(await Evaluate(db, SnapshotScript,
                [Key("epoch"), Aggregate(conversation, member.Key), Revision(conversation, member.Key),
                    Lifetime(conversation, member.Key), State(conversation, member.Key), Key("typing-epoch")],
                [epoch, conversation.ToString("N"), member.Value.ToString("N"), Key("connection:"), Key("subscriptions:"),
                    activityEpoch, Key("owner:")], token));
            if ((int)result[0] < 0) throw new HubException("Typing unavailable; retry after recovery.");
            if ((int)result[0] > 0)
                items.Add(new(member.Key, (int)result[0], (long)result[1], (string?)result[2]));
        }
        return new(conversation, items, 0, $"{epoch}:{activityEpoch}");
    }

    // Caller holds the PostgreSQL conversation lock and provides its current membership incarnation map.
    public async Task Publish(TypingChanged change, IReadOnlyDictionary<Guid, Guid> members, CancellationToken token)
    {
        if (!members.ContainsKey(change.UserId)) return;
        var current = await SnapshotState(change.ConversationId, change.UserId, members[change.UserId], token);
        if (current.Epoch != change.Epoch || current.Lifetime != change.Lifetime || current.Version != change.Version
            || current.ExpiresInMilliseconds == 0 && change.ExpiresInMilliseconds > 0)
            return;
        try
        {
            var recipients = await registry.Subscribers(change.ConversationId, members, token);
            await hub.Clients.Clients(recipients).SendAsync(ConversationTyping.EventName,
                change with { ExpiresInMilliseconds = current.ExpiresInMilliseconds }, token);
        }
        catch (Exception error) when (error is RedisException or TimeoutException)
        {
            MarkUncertain();
            logger.LogWarning(error, "Typing publication uncertain; previous activity will be fenced after recovery.");
            throw new HubException("Typing unavailable; retry after recovery.");
        }
    }

    private async Task<(int ExpiresInMilliseconds, long Version, string? Lifetime, string Epoch)> SnapshotState(
        Guid conversation, Guid user, Guid membership, CancellationToken token)
    {
        var (epoch, activityEpoch, db) = await Epochs(token);
        var result = Result(await Evaluate(db, SnapshotScript,
            [Key("epoch"), Aggregate(conversation, user), Revision(conversation, user), Lifetime(conversation, user), State(conversation, user), Key("typing-epoch")],
            [epoch, conversation.ToString("N"), membership.ToString("N"), Key("connection:"), Key("subscriptions:"),
                activityEpoch, Key("owner:")], token));
        return ((int)result[0], (long)result[1], (string?)result[2], $"{epoch}:{activityEpoch}");
    }
}
