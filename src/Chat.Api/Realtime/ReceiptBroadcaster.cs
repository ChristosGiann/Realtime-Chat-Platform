using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

public sealed record ReceiptAdvanced(Guid ConversationId, MemberReceipt Member);
public interface IReceiptBroadcaster
{
    Task Advanced(Guid conversation, Guid membership, CancellationToken token);
}

public sealed class ReceiptBroadcaster(IServiceScopeFactory scopes, ConversationConnections connections,
    IHubContext<ChatHub> hub) : IReceiptBroadcaster
{
    public const string EventName = "ReceiptAdvanced";
    public async Task Advanced(Guid conversation, Guid membership, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleOrDefaultAsync(token);
        var members = await ReceiptSnapshot.Members(db, conversation, 0, int.MaxValue, token);
        // A removed/rejoined membership must not publish a previous incarnation's progress.
        var current = members.SingleOrDefault(x => x.MembershipId == membership);
        if (current is not null)
            await hub.Clients.Clients(connections.Subscribers(conversation, members.Select(x => x.UserId).ToArray()))
                .SendAsync(EventName, new ReceiptAdvanced(conversation, current), token);
        await transaction.CommitAsync(token);
    }
}

public static class PostCommitNotification
{
    public static async Task Send(Func<CancellationToken, Task> publish, ILogger logger)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await publish(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Post-commit notification failed; REST recovers durable state.");
        }
    }
}
