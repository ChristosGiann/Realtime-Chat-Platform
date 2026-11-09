using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Chat.Api.Realtime;

// Ephemeral single-instance routing state. PostgreSQL remains the authorization authority.
public sealed class ConversationConnections(IHubContext<ChatHub> hub)
{
    private sealed record Connection(Guid UserId, Action Abort, DateTimeOffset? ExpiresAt)
    {
        public ConcurrentDictionary<Guid, byte> Conversations { get; } = new();
    }
    private readonly ConcurrentDictionary<string, Connection> connections = new();
    private readonly object presenceLock = new();
    private readonly Dictionary<Guid, PresenceChange> presence = new();
    private long version;
    private static bool Active(Connection connection) => connection.ExpiresAt is null || connection.ExpiresAt > DateTimeOffset.UtcNow;
    public bool IsActive(string id) => connections.TryGetValue(id, out var connection) && Active(connection);

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
        await hub.Groups.AddToGroupAsync(id, ConversationGroup.Name(conversation), token);
        connection.Conversations[conversation] = 0;
    }
    public async Task Unsubscribe(string id, Guid conversation, CancellationToken token)
    {
        await hub.Groups.RemoveFromGroupAsync(id, ConversationGroup.Name(conversation), token);
        // Keep routing metadata until removal succeeds, so a later revocation can still find it.
        if (connections.TryGetValue(id, out var connection)) connection.Conversations.TryRemove(conversation, out _);
    }
    public async Task Revoke(Guid conversation, Guid userId, CancellationToken token)
    {
        foreach (var pair in connections.Where(x => x.Value.UserId == userId))
        {
            if (!pair.Value.Conversations.TryRemove(conversation, out _)) continue;
            try { await hub.Groups.RemoveFromGroupAsync(pair.Key, ConversationGroup.Name(conversation), token); }
            catch { pair.Value.Abort(); throw; }
        }
    }
}
