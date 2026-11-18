using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public sealed record ConversationMembershipChanged(Guid ConversationId);
public sealed record ConversationAccessRevoked(Guid ConversationId, Guid MembershipId);

public sealed class MembershipNotifications(IServiceScopeFactory scopes, ConversationConnections connections,
    DistributedConnectionRegistry distributed, IHubContext<ChatHub> hub)
{
    public async Task Changed(Guid conversation, Guid user, Guid? removedMembership, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleOrDefaultAsync(token);
        var members = await db.ConversationMembers.Where(x => x.ConversationId == conversation)
            .Select(x => new { x.UserId, x.MembershipId }).ToDictionaryAsync(x => x.UserId, x => x.MembershipId, token);
        await hub.Clients.Clients(distributed.Enabled
                ? await distributed.Subscribers(conversation, members, token)
                : connections.Subscribers(conversation, members.Keys.ToArray()))
            .SendAsync("ConversationMembershipChanged", new ConversationMembershipChanged(conversation), token);
        if (removedMembership is { } old)
            await hub.Clients.Clients(distributed.Enabled
                    ? await distributed.ConnectionsForUser(user, token)
                    : connections.ForUser(user))
                .SendAsync("ConversationAccessRevoked", new ConversationAccessRevoked(conversation, old), token);
        await transaction.CommitAsync(token);
    }
}
