using Chat.Api.Messages;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public interface IMessageBroadcaster
{
    Task MessageCreatedAsync(MessageResponse message, CancellationToken cancellationToken);
}

public sealed class MessageBroadcaster(IHubContext<ChatHub> hub, IServiceScopeFactory scopes) : IMessageBroadcaster
{
    public const string EventName = "MessageCreated";
    public async Task MessageCreatedAsync(MessageResponse message, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {message.ConversationId} FOR UPDATE").SingleAsync(cancellationToken);
        await hub.Clients.Group(ConversationGroup.Name(message.ConversationId)).SendAsync(EventName, message, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
