using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public sealed record ConversationMembershipChanged(Guid ConversationId);
public sealed record ConversationAccessRevoked(Guid ConversationId, Guid MembershipId);

public sealed class MembershipNotifications(IServiceScopeFactory scopes, ConversationConnections connections, IHubContext<ChatHub> hub)
{
    public async Task Changed(Guid conversation, Guid user, Guid? removedMembership, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleOrDefaultAsync(token);
        var members = await db.ConversationMembers.Where(x => x.ConversationId == conversation).Select(x => x.UserId).ToListAsync(token);
        await hub.Clients.Clients(connections.Subscribers(conversation, members))
            .SendAsync("ConversationMembershipChanged", new ConversationMembershipChanged(conversation), token);
        if (removedMembership is { } old)
            await hub.Clients.Clients(connections.ForUser(user))
                .SendAsync("ConversationAccessRevoked", new ConversationAccessRevoked(conversation, old), token);
        await transaction.CommitAsync(token);
    }
}
