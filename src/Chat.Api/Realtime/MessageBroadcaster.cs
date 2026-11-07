using Chat.Api.Messages;
using Microsoft.AspNetCore.SignalR;

namespace Chat.Api.Realtime;

public interface IMessageBroadcaster
{
    Task MessageCreatedAsync(MessageResponse message, CancellationToken cancellationToken);
}

public sealed class MessageBroadcaster(IHubContext<ChatHub> hub) : IMessageBroadcaster
{
    public const string EventName = "MessageCreated";
    public Task MessageCreatedAsync(MessageResponse message, CancellationToken cancellationToken) =>
        hub.Clients.Group(ConversationGroup.Name(message.ConversationId)).SendAsync(EventName, message, cancellationToken);
}
