using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public sealed record PresenceMember(Guid UserId, bool IsOnline);
public sealed record PresencePage(Guid ConversationId, IReadOnlyList<PresenceMember> Items, int? NextOffset, long Version);
public sealed record PresenceChange(Guid UserId, bool IsOnline, long Version);
public sealed record ConversationPresenceChanged(Guid ConversationId, Guid UserId, bool IsOnline, long Version);

// Only ephemeral projections. Membership authorization is read from PostgreSQL under the mutation lock.
public sealed class ConversationPresence(ConversationConnections connections, IServiceScopeFactory scopes,
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
                var members = await db.ConversationMembers.Where(x => x.ConversationId == id).Select(x => x.UserId).ToListAsync();
                if (members.Contains(change.UserId) && connections.IsCurrent(change))
                {
                    var recipients = connections.Subscribers(id, members);
                    await hub.Clients.Clients(recipients).SendAsync(EventName,
                        new ConversationPresenceChanged(id, change.UserId, change.IsOnline, change.Version));
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
