using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Chat.Api.Realtime;

// Ephemeral single-instance routing state. PostgreSQL remains the authorization authority.
public sealed class ConversationConnections(IHubContext<ChatHub> hub, IGroupManager? groupOverride = null)
{
    private readonly IGroupManager groups = groupOverride ?? hub.Groups;
    private sealed record Connection(Guid UserId, Action Abort, DateTimeOffset? ExpiresAt)
    {
        public ConcurrentDictionary<Guid, long> Conversations { get; } = new();
    }
    private readonly ConcurrentDictionary<string, Connection> connections = new();
    private readonly object presenceLock = new();
    private readonly Dictionary<Guid, PresenceChange> presence = new();
    private long version;
    private long subscriptionVersion;
    private static bool Active(Connection connection) => connection.ExpiresAt is null || connection.ExpiresAt > DateTimeOffset.UtcNow;
    public bool IsActive(string id) => connections.TryGetValue(id, out var connection) && Active(connection);
    public IReadOnlyList<string> ForUser(Guid user) => connections.Where(x => x.Value.UserId == user && Active(x.Value)).Select(x => x.Key).ToArray();
    public long? Subscription(string id, Guid conversation) => connections.TryGetValue(id, out var connection)
        && Active(connection) && connection.Conversations.TryGetValue(conversation, out var generation) ? generation : null;
    public bool Matches(string id, Guid user, Guid conversation, long generation) =>
        connections.TryGetValue(id, out var connection) && connection.UserId == user
        && connection.Conversations.TryGetValue(conversation, out var current) && current == generation;

    // The remote handler never takes the PostgreSQL row lock. Only a captured generation can be
    // removed; a later Subscribe (or rejoin) always owns a different generation.
    public async Task RevokeExact(string id, Guid user, Guid conversation, long generation, CancellationToken token)
    {
        if (!connections.TryGetValue(id, out var connection) || connection.UserId != user
            || !connection.Conversations.TryRemove(new KeyValuePair<Guid, long>(conversation, generation))) return;
        try { await groups.RemoveFromGroupAsync(id, ConversationGroup.Name(conversation), token); }
        catch { connection.Abort(); throw; }
    }

    public PresenceChange? Connected(string id, Guid userId, Action abort, DateTimeOffset? expiresAt = null)
    {
        lock (presenceLock)
        {
            connections[id] = new(userId, abort, expiresAt);
            return UpdatePresence(userId);
        }
    }
    public PresenceChange? Disconnected(string id)
    {
        lock (presenceLock)
            return connections.TryRemove(id, out var connection) ? UpdatePresence(connection.UserId) : null;
    }
    private PresenceChange? UpdatePresence(Guid userId)
    {
        var online = connections.Values.Any(x => x.UserId == userId && Active(x));
        if (presence.TryGetValue(userId, out var previous) && previous.IsOnline == online) return null;
        return presence[userId] = new(userId, online, ++version);
    }
    public bool IsCurrent(PresenceChange change)
    {
        lock (presenceLock)
            return presence.GetValueOrDefault(change.UserId) == change
                && connections.Values.Any(x => x.UserId == change.UserId && Active(x)) == change.IsOnline;
    }
    public PresencePage Snapshot(Guid conversation, IReadOnlyList<Guid> users, int? nextOffset)
    {
        lock (presenceLock)
        {
            var online = connections.Values.Where(Active).Select(x => x.UserId).ToHashSet();
            // Snapshot watermark supersedes earlier in-flight events, even if an expiry callback is pending.
            return new(conversation, users.Select(id => new PresenceMember(id, online.Contains(id))).ToArray(), nextOffset, ++version);
        }
    }
    public IReadOnlyList<string> Subscribers(Guid conversation, IReadOnlyCollection<Guid> members) => connections
        .Where(x => Active(x.Value) && members.Contains(x.Value.UserId) && x.Value.Conversations.ContainsKey(conversation))
        .Select(x => x.Key).ToArray();

    // Caller holds the conversation row lock, also used by membership mutations and broadcasts.
    public async Task Subscribe(string id, Guid conversation, CancellationToken token)
    {
        if (!connections.TryGetValue(id, out var connection)) throw new HubException("Connection unavailable.");
        await groups.AddToGroupAsync(id, ConversationGroup.Name(conversation), token);
        connection.Conversations.AddOrUpdate(conversation, _ => Interlocked.Increment(ref subscriptionVersion),
            (_, _) => Interlocked.Increment(ref subscriptionVersion));
    }
    public async Task Unsubscribe(string id, Guid conversation, CancellationToken token)
    {
        await groups.RemoveFromGroupAsync(id, ConversationGroup.Name(conversation), token);
        // Keep routing metadata until removal succeeds, so a later revocation can still find it.
        if (connections.TryGetValue(id, out var connection)) connection.Conversations.TryRemove(conversation, out _);
    }
    public async Task Revoke(Guid conversation, Guid userId, CancellationToken token)
    {
        foreach (var pair in connections.Where(x => x.Value.UserId == userId))
        {
            if (!pair.Value.Conversations.TryRemove(conversation, out _)) continue;
            try { await groups.RemoveFromGroupAsync(pair.Key, ConversationGroup.Name(conversation), token); }
            catch { pair.Value.Abort(); throw; }
        }
    }
}
