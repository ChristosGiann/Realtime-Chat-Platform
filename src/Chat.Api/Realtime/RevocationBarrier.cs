using System.Diagnostics;
using System.Text.Json;
using Chat.Api.Infrastructure.Redis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Chat.Api.Realtime;

public sealed record RevocationCommand(Guid OperationId, Guid ConversationId, Guid UserId, Guid MembershipId,
    string Epoch, string Owner, long DeadlineMilliseconds,
    IReadOnlyList<DistributedConnectionRegistry.RevocationConnection> Connections);

// Redis is an ephemeral command/ACK channel. The PostgreSQL transaction remains the authority.
public sealed class RevocationBarrier(DistributedConnectionRegistry registry, ConversationConnections local,
    DistributedTyping typing, RedisConnection connection, RedisNamespace names, IOptions<RedisOptions> options,
    ILogger<RevocationBarrier> logger) : BackgroundService
{
    private string Key(string suffix) => names.CoordinationPrefix + suffix;
    private RedisKey Operation(Guid id) => Key($"revoke-operation:{id:N}");
    private RedisKey Ack(Guid id, string owner) => Key($"revoke-ack:{id:N}:{owner}");
    private RedisChannel Channel(string owner) => RedisChannel.Literal(Key($"revoke-command:{owner}"));
    private TimeSpan Timeout => TimeSpan.FromMilliseconds(options.Value.OperationTimeoutMilliseconds);
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly Dictionary<Guid, (string Status, DateTimeOffset Expires)> outcomes = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!registry.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var redis = await connection.GetAsync(stoppingToken);
                var queue = await redis.GetSubscriber().SubscribeAsync(Channel(registry.Owner)).WaitAsync(Timeout, stoppingToken);
                queue.OnMessage(async message =>
                {
                    try
                    {
                        var command = JsonSerializer.Deserialize<RevocationCommand>((string)message.Message!);
                        if (command is not null) await Handle(command, stoppingToken);
                    }
                    catch (Exception error) { logger.LogWarning(error, "Revocation command could not be acknowledged."); }
                });
                using var sweep = new PeriodicTimer(TimeSpan.FromSeconds(30));
                while (await sweep.WaitForNextTickAsync(stoppingToken))
                {
                    await commandGate.WaitAsync(stoppingToken);
                    try
                    {
                        foreach (var expired in outcomes.Where(x => x.Value.Expires <= DateTimeOffset.UtcNow)
                            .Select(x => x.Key).ToArray()) outcomes.Remove(expired);
                    }
                    finally { commandGate.Release(); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is RedisException or TimeoutException)
            {
                logger.LogWarning(error, "Revocation command channel unavailable; membership changes fail closed.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task Handle(RevocationCommand command, CancellationToken token)
    {
        if (command.Owner != registry.Owner || command.OperationId == Guid.Empty) return;
        await commandGate.WaitAsync(token);
        try
        {
            foreach (var expired in outcomes.Where(x => x.Value.Expires <= DateTimeOffset.UtcNow)
                .Select(x => x.Key).ToArray()) outcomes.Remove(expired);
            if (!outcomes.TryGetValue(command.OperationId, out var prior))
            {
                // Exhaustion reduces availability; it never turns a missing ACK into success.
                if (outcomes.Count >= 4096) return;
                var status = await Process(command, token);
                if (status is null) return;
                prior = (status, DateTimeOffset.UtcNow.AddSeconds(30));
                outcomes[command.OperationId] = prior;
            }
            await WriteAck(command, prior.Status, token);
        }
        finally { commandGate.Release(); }
    }

    private async Task<string?> Process(RevocationCommand command, CancellationToken token)
    {
        try
        {
            var redis = await connection.GetAsync(token);
            var db = redis.GetDatabase();
            var valid = (int)await db.ScriptEvaluateAsync("""
                local time = redis.call('TIME')
                local now = time[1] * 1000 + math.floor(time[2] / 1000)
                if now >= tonumber(ARGV[3]) or redis.call('GET', KEYS[1]) ~= ARGV[1]
                    or redis.call('GET', KEYS[2]) ~= ARGV[2] then return 0 end
                return 1
                """, [Key("epoch"), Operation(command.OperationId)],
                [command.Epoch, $"{command.MembershipId:N}:{command.Epoch}:{command.DeadlineMilliseconds}", command.DeadlineMilliseconds])
                .WaitAsync(Timeout, token);
            if (valid != 1) return null;
            var entries = command.Connections.Where(x => x.Owner == registry.Owner).ToArray();
            var expected = entries.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in local.ForUser(command.UserId))
                if (local.Subscription(id, command.ConversationId) is not null && !expected.Contains(id))
                    throw new InvalidOperationException("Uncaptured local subscription.");
            foreach (var entry in entries)
            {
                var parts = entry.Subscription.Split(':');
                if (parts.Length != 4 || parts[0] != command.MembershipId.ToString("N")
                    || !long.TryParse(parts[1], out var generation)
                    || parts[2] + ":" + parts[3] != registry.Owner)
                    throw new InvalidOperationException("Revocation generation mismatch.");
                await registry.UnsubscribeExact(entry.Id, command.ConversationId, entry.Subscription,
                    command.Epoch, token);
                await local.RevokeExact(entry.Id, command.UserId, command.ConversationId, generation, token);
                typing.ForgetSubscription(entry.Id, command.ConversationId);
            }
            return "ACK";
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Revocation owner could not prove local fencing.");
            return "NACK";
        }
    }

    private async Task WriteAck(RevocationCommand command, string status, CancellationToken token)
    {
        try
        {
            var db = (await connection.GetAsync(token)).GetDatabase();
            // Late responses and changed epochs are rejected atomically, not accepted by the coordinator.
            await db.ScriptEvaluateAsync("""
                local time = redis.call('TIME')
                local now = time[1] * 1000 + math.floor(time[2] / 1000)
                if now >= tonumber(ARGV[3]) or redis.call('GET', KEYS[1]) ~= ARGV[1]
                    or redis.call('GET', KEYS[2]) ~= ARGV[2] then return 0 end
                if redis.call('GET', KEYS[3]) == 'NACK' then return 1 end
                redis.call('SET', KEYS[3], ARGV[4], 'PX', 30000)
                return 1
                """, [Key("epoch"), Operation(command.OperationId), Ack(command.OperationId, registry.Owner)],
                [command.Epoch, $"{command.MembershipId:N}:{command.Epoch}:{command.DeadlineMilliseconds}",
                    command.DeadlineMilliseconds, status]).WaitAsync(Timeout, token);
        }
        catch (Exception error) { logger.LogWarning(error, "Revocation ACK could not be stored."); }
    }

    public async Task Revoke(DistributedConnectionRegistry.RevocationSnapshot snapshot,
        Guid conversation, Guid user, Guid membership, CancellationToken token)
    {
        if (!registry.Enabled) { await local.Revoke(conversation, user, token); return; }
        var operation = Guid.NewGuid();
        var stopwatch = Stopwatch.StartNew();
        var redis = await connection.GetAsync(token);
        var db = redis.GetDatabase();
        var deadline = (long)await db.ScriptEvaluateAsync("""
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
            local time = redis.call('TIME')
            local deadline = time[1] * 1000 + math.floor(time[2] / 1000) + 5000
            redis.call('SET', KEYS[2], ARGV[2] .. ':' .. ARGV[1] .. ':' .. deadline, 'PX', 30000)
            return deadline
            """, [Key("epoch"), Operation(operation)], [snapshot.Epoch, membership.ToString("N")])
            .WaitAsync(Timeout, token);
        if (deadline == 0) throw new InvalidOperationException("Coordination epoch changed.");
        var subscriber = redis.GetSubscriber();
        foreach (var owner in snapshot.Owners)
        {
            var command = new RevocationCommand(operation, conversation, user, membership, snapshot.Epoch,
                owner, deadline, snapshot.Connections.Where(x => x.Owner == owner).ToArray());
            await subscriber.PublishAsync(Channel(owner), JsonSerializer.Serialize(command)).WaitAsync(Timeout, token);
        }
        var pending = snapshot.Owners.ToHashSet(StringComparer.Ordinal);
        while (pending.Count != 0)
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("Revocation ACK deadline expired.");
            if (await registry.RequireEpoch(token) != snapshot.Epoch)
                throw new InvalidOperationException("Coordination epoch changed.");
            foreach (var owner in pending.ToArray())
            {
                var ack = await db.StringGetAsync(Ack(operation, owner)).WaitAsync(Timeout, token);
                if (ack.IsNull) continue;
                if (ack != "ACK") throw new InvalidOperationException("Revocation owner NACK.");
                pending.Remove(owner);
            }
            if (pending.Count != 0) await Task.Delay(40, token);
        }
        if (stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("Revocation ACK deadline expired.");
        var keys = new RedisKey[] { Key("epoch"), Operation(operation) }
            .Concat(snapshot.Owners.Select(owner => Ack(operation, owner))).ToArray();
        var verified = (int)await db.ScriptEvaluateAsync("""
            local time = redis.call('TIME')
            local now = time[1] * 1000 + math.floor(time[2] / 1000)
            if now >= tonumber(ARGV[3]) or redis.call('GET', KEYS[1]) ~= ARGV[1]
                or redis.call('GET', KEYS[2]) ~= ARGV[2] then return 0 end
            for i = 3, #KEYS do
                if redis.call('GET', KEYS[i]) ~= 'ACK' then return 0 end
            end
            return 1
            """, keys, [snapshot.Epoch, $"{membership:N}:{snapshot.Epoch}:{deadline}", deadline])
            .WaitAsync(Timeout, token);
        if (verified != 1) throw new InvalidOperationException("Revocation coordination uncertain.");
    }
}
