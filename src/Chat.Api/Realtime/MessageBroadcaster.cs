using Chat.Api.Messages;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public interface IMessageBroadcaster
{
    Task MessageCreatedAsync(MessageResponse message, CancellationToken cancellationToken);
}

public sealed class MessageBroadcaster(IHubContext<ChatHub> hub, IServiceScopeFactory scopes,
    ConversationConnections connections, DistributedConnectionRegistry distributed) : IMessageBroadcaster
{
    public const string EventName = "MessageCreated";
    public async Task MessageCreatedAsync(MessageResponse message, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {message.ConversationId} FOR UPDATE").SingleAsync(cancellationToken);
        var members = await db.ConversationMembers.Where(x => x.ConversationId == message.ConversationId)
            .Select(x => new { x.UserId, x.MembershipId })
            .ToDictionaryAsync(x => x.UserId, x => x.MembershipId, cancellationToken);
        var recipients = distributed.Enabled
            ? await distributed.Subscribers(message.ConversationId, members, cancellationToken)
            : connections.Subscribers(message.ConversationId, members.Keys.ToArray());
        await hub.Clients.Clients(recipients).SendAsync(EventName, message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
