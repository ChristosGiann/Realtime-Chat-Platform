using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public sealed record PresenceMember(Guid UserId, bool IsOnline, long Revision = 0, string? Lifetime = null);
public sealed record PresencePage(Guid ConversationId, IReadOnlyList<PresenceMember> Items, int? NextOffset, long Version,
    string? Epoch = null, string? RosterVersion = null);
public sealed record PresenceChange(Guid UserId, bool IsOnline, long Version, string? Lifetime = null, string? Epoch = null);
public sealed record ConversationPresenceChanged(Guid ConversationId, Guid UserId, bool IsOnline, long Version, string? Epoch = null, string? Lifetime = null);

// Only ephemeral projections. Membership authorization is read from PostgreSQL under the mutation lock.
public sealed class ConversationPresence(ConversationConnections connections, DistributedConnectionRegistry distributed, IServiceScopeFactory scopes,
    IHubContext<ChatHub> hub, ILogger<ConversationPresence> logger)
{
    public const string EventName = "PresenceChanged";

    public async Task Publish(PresenceChange? change)
    {
        if (change is null) return;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var conversations = await db.ConversationMembers.AsNoTracking().Where(x => x.UserId == change.UserId)
                .Select(x => x.ConversationId).ToListAsync();
            foreach (var id in conversations)
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {id} FOR UPDATE")
                    .SingleOrDefaultAsync();
                var members = await db.ConversationMembers.Where(x => x.ConversationId == id)
                    .Select(x => new { x.UserId, x.MembershipId }).ToDictionaryAsync(x => x.UserId, x => x.MembershipId);
                var current = connections.IsCurrent(change);
                if (distributed.Enabled)
                {
                    var snapshot = await distributed.Snapshot([change.UserId], CancellationToken.None);
                    var state = snapshot.Members[change.UserId];
                    current = snapshot.Epoch == change.Epoch && state.Lifetime == change.Lifetime
                        && state.Revision == change.Version && state.IsOnline == change.IsOnline;
                }
                if (members.ContainsKey(change.UserId) && current)
                {
                    var recipients = distributed.Enabled
                        ? await distributed.Subscribers(id, members, CancellationToken.None)
                        : connections.Subscribers(id, members.Keys.ToArray());
                    await hub.Clients.Clients(recipients).SendAsync(EventName,
                        new ConversationPresenceChanged(id, change.UserId, change.IsOnline, change.Version,
                            change.Epoch, change.Lifetime));
                }
                await transaction.CommitAsync();
            }
        }
        catch (Exception exception)
        {
            // A failed ephemeral notification cannot prevent connection cleanup or become durable state.
            logger.LogWarning(exception, "Presence notification failed; clients recover with an authorized snapshot.");
        }
    }
}
