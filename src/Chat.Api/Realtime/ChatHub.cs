using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Chat.Api.Realtime;

[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
public sealed class ChatHub(ChatSessionValidator sessions, ChatDbContext db, ConversationAuthorization authorization,
    ConversationConnections connections, ConversationPresence presence, ConversationTyping typing) : Hub
{
    public const string Endpoint = "/hubs/chat";

    public override async Task OnConnectedAsync()
    {
        if (!await sessions.IsValidAsync(Context.User)) { Context.Abort(); return; }
        var ticket = await Context.GetHttpContext()!.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (ticket.Properties?.ExpiresUtc <= DateTimeOffset.UtcNow) { Context.Abort(); return; }
        await presence.Publish(connections.Connected(Context.ConnectionId, sessions.GetUserId(Context.User)!.Value,
            Context.Abort, ticket.Properties?.ExpiresUtc));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await presence.Publish(connections.Disconnected(Context.ConnectionId));
        await base.OnDisconnectedAsync(exception);
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

    public async Task<PresencePage> GetPresence(Guid conversationId, int offset, int limit)
    {
        var actor = await ValidActor();
        if (conversationId == Guid.Empty || offset < 0 || limit is < 1 or > 100)
            throw new HubException("Conversation not available.");
        await using var transaction = await db.Database.BeginTransactionAsync(Context.ConnectionAborted);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (!await authorization.ReadableConversationsFor(actor).AnyAsync(x => x.Id == conversationId, Context.ConnectionAborted))
            throw new HubException("Conversation not available.");
        var users = await db.ConversationMembers.Where(x => x.ConversationId == conversationId).OrderBy(x => x.UserId)
            .Skip(offset).Take(limit + 1).Select(x => x.UserId).ToListAsync(Context.ConnectionAborted);
        var result = connections.Snapshot(conversationId, users.Take(limit).ToArray(), users.Count > limit ? offset + limit : null);
        await transaction.CommitAsync(Context.ConnectionAborted);
        return result;
    }

    public async Task SetTyping(Guid conversationId, bool active)
    {
        var actor = await ValidActor();
        await using var transaction = await db.Database.BeginTransactionAsync(Context.ConnectionAborted);
        var members = await TypingMembers(conversationId, actor);
        if (typing.Change(Context.ConnectionId, conversationId, actor, active))
            await typing.Publish(conversationId, actor, members, Context.ConnectionAborted);
        await transaction.CommitAsync(Context.ConnectionAborted);
    }

    public async Task<TypingSnapshot> GetTyping(Guid conversationId)
    {
        var actor = await ValidActor();
        await using var transaction = await db.Database.BeginTransactionAsync(Context.ConnectionAborted);
        var members = await TypingMembers(conversationId, actor);
        var snapshot = typing.Snapshot(conversationId, members);
        await transaction.CommitAsync(Context.ConnectionAborted);
        return snapshot;
    }

    private async Task<List<Guid>> TypingMembers(Guid conversationId, Guid actor)
    {
        if (conversationId == Guid.Empty) throw new HubException("Conversation not available.");
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(Context.ConnectionAborted);
        if (connections.Subscription(Context.ConnectionId, conversationId) is null
            || !await authorization.ReadableConversationsFor(actor).AnyAsync(x => x.Id == conversationId, Context.ConnectionAborted))
            throw new HubException("Conversation not available.");
        return await db.ConversationMembers.Where(x => x.ConversationId == conversationId).Select(x => x.UserId).ToListAsync(Context.ConnectionAborted);
    }

    private async Task<Guid> ValidActor()
    {
        if (!connections.IsActive(Context.ConnectionId) || !await sessions.IsValidAsync(Context.User))
        {
            Context.Abort();
            await presence.Publish(connections.Disconnected(Context.ConnectionId));
            throw new HubException("Authentication required.");
        }
        return sessions.GetUserId(Context.User)!.Value;
    }
}
