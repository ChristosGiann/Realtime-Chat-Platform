using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Chat.Api.Realtime;

// Ephemeral single-instance routing state. PostgreSQL remains the authorization authority.
public sealed class ConversationConnections(IHubContext<ChatHub> hub)
{
    private sealed record Connection(Guid UserId, Action Abort)
    {
        public ConcurrentDictionary<Guid, byte> Conversations { get; } = new();
    }
    private readonly ConcurrentDictionary<string, Connection> connections = new();
    public void Connected(string id, Guid userId, Action abort) => connections[id] = new(userId, abort);
    public void Disconnected(string id) => connections.TryRemove(id, out _);

    // Caller holds the conversation row lock, also used by membership mutations and broadcasts.
    public async Task Subscribe(string id, Guid conversation, CancellationToken token)
    {
        if (!connections.TryGetValue(id, out var connection)) throw new HubException("Connection unavailable.");
        await hub.Groups.AddToGroupAsync(id, ConversationGroup.Name(conversation), token);
        connection.Conversations[conversation] = 0;
    }
    public async Task Unsubscribe(string id, Guid conversation, CancellationToken token)
    {
        if (connections.TryGetValue(id, out var connection)) connection.Conversations.TryRemove(conversation, out _);
        await hub.Groups.RemoveFromGroupAsync(id, ConversationGroup.Name(conversation), token);
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
