using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public sealed record TypingMember(Guid UserId, int ExpiresInMilliseconds);
public sealed record TypingSnapshot(Guid ConversationId, IReadOnlyList<TypingMember> Items, long Version);
public sealed record TypingChanged(Guid ConversationId, Guid UserId, int ExpiresInMilliseconds, long Version);

public sealed class ConversationTyping(ConversationConnections connections, TimeProvider clock, IServiceScopeFactory scopes,
    IHubContext<ChatHub> hub, ILogger<ConversationTyping> logger) : BackgroundService
{
    public const string EventName = "TypingChanged";
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(5);
    private sealed record Lease(Guid UserId, long Subscription, long AcceptedAt);
    private readonly object gate = new();
    private readonly Dictionary<(string Connection, Guid Conversation), Lease> leases = new();
    private readonly Dictionary<(string Connection, Guid Conversation), long> accepted = new();
    private long version;

    private int Remaining((string Connection, Guid Conversation) key, Lease lease)
    {
        if (connections.Subscription(key.Connection, key.Conversation) != lease.Subscription) return 0;
        return (int)Math.Clamp(Math.Ceiling((LeaseDuration - clock.GetElapsedTime(lease.AcceptedAt)).TotalMilliseconds), 0, 5000);
    }
    // Called under the PostgreSQL conversation lock after session/member/subscription validation.
    public bool Change(string connection, Guid conversation, Guid actor, bool active)
    {
        lock (gate)
        {
            var key = (connection, conversation);
            if (!active) return leases.Remove(key); // Stop is never throttled.
            if (accepted.TryGetValue(key, out var last) && clock.GetElapsedTime(last) < TimeSpan.FromSeconds(1)) return false;
            var generation = connections.Subscription(connection, conversation);
            if (generation is null) throw new HubException("Conversation not available.");
            var now = clock.GetTimestamp();
            accepted[key] = now; leases[key] = new(actor, generation.Value, now);
            return true;
        }
    }
    public TypingSnapshot Snapshot(Guid conversation, IReadOnlyCollection<Guid> members)
    {
        lock (gate)
        {
            var items = leases.Where(x => x.Key.Conversation == conversation && members.Contains(x.Value.UserId))
                .Select(x => new TypingMember(x.Value.UserId, Remaining(x.Key, x.Value))).Where(x => x.ExpiresInMilliseconds > 0)
                .GroupBy(x => x.UserId).Select(g => new TypingMember(g.Key, g.Max(x => x.ExpiresInMilliseconds))).ToArray();
            return new(conversation, items, ++version);
        }
    }
    // Caller holds the membership mutation lock. Other tabs of the same actor are aggregated.
    public Task Publish(Guid conversation, Guid user, IReadOnlyCollection<Guid> members, CancellationToken token)
    {
        if (!members.Contains(user)) return Task.CompletedTask;
        TypingChanged change;
        lock (gate)
        {
            var remaining = leases.Where(x => x.Key.Conversation == conversation && x.Value.UserId == user)
                .Select(x => Remaining(x.Key, x.Value)).DefaultIfEmpty(0).Max();
            change = new(conversation, user, remaining, ++version);
        }
        return hub.Clients.Clients(connections.Subscribers(conversation, members)).SendAsync(EventName, change, token);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                (Guid Conversation, Guid User)[] expired;
                lock (gate)
                {
                    var removed = leases.Where(x => Remaining(x.Key, x.Value) == 0).ToArray();
                    expired = removed.Select(x => (x.Key.Conversation, x.Value.UserId)).Distinct().ToArray();
                    foreach (var item in removed) leases.Remove(item.Key);
                    foreach (var key in accepted.Where(x => !connections.IsActive(x.Key.Connection)
                        || clock.GetElapsedTime(x.Value) >= LeaseDuration).Select(x => x.Key).ToArray()) accepted.Remove(key);
                }
                foreach (var item in expired)
                {
                    try
                    {
                        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
                        await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {item.Conversation} FOR UPDATE")
                            .SingleOrDefaultAsync(stoppingToken);
                        var members = await db.ConversationMembers.Where(x => x.ConversationId == item.Conversation)
                            .Select(x => x.UserId).ToListAsync(stoppingToken);
                        await Publish(item.Conversation, item.User, members, stoppingToken);
                        await transaction.CommitAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception) { logger.LogWarning(exception, "Typing expiry notification failed; receiver lease still expires."); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
