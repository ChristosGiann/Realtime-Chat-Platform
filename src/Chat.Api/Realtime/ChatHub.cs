using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Realtime;

[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
public sealed class ChatHub(ChatSessionValidator sessions, ChatDbContext db, ConversationAuthorization authorization, ConversationConnections connections) : Hub
{
    public const string Endpoint = "/hubs/chat";

    public override async Task OnConnectedAsync()
    {
        if (!await sessions.IsValidAsync(Context.User)) { Context.Abort(); return; }
        connections.Connected(Context.ConnectionId, sessions.GetUserId(Context.User)!.Value, Context.Abort);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task Subscribe(Guid conversationId)
    {
        var actor = await ValidActor();
        if (conversationId == Guid.Empty) throw new HubException("Conversation not available.");
        await using var transaction = await db.Database.BeginTransactionAsync(Context.ConnectionAborted);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (!await authorization.ReadableConversationsFor(actor).AnyAsync(x => x.Id == conversationId, Context.ConnectionAborted))
            throw new HubException("Conversation not available.");
        try
        {
            await connections.Subscribe(Context.ConnectionId, conversationId, Context.ConnectionAborted);
            await transaction.CommitAsync(Context.ConnectionAborted);
        }
        catch { Context.Abort(); throw; }
    }

    public async Task Unsubscribe(Guid conversationId)
    {
        await ValidActor();
        if (conversationId == Guid.Empty) throw new HubException("Conversation not available.");
        // Removal affects only this connection, and is safe even after membership was removed.
        await connections.Unsubscribe(Context.ConnectionId, conversationId, Context.ConnectionAborted);
    }

    private async Task<Guid> ValidActor()
    {
        if (!await sessions.IsValidAsync(Context.User))
        { Context.Abort(); throw new HubException("Authentication required."); }
        return sessions.GetUserId(Context.User)!.Value;
    }
}
