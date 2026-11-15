using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Chat.Api.Messages;

namespace Chat.Api.Realtime;

[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
public sealed class ChatHub(ChatSessionValidator sessions, ChatDbContext db, ConversationAuthorization authorization,
    ConversationConnections connections, DistributedConnectionRegistry distributed, ConversationPresence presence, ConversationTyping typing) : Hub
{
    public const string Endpoint = "/hubs/chat";

    public override async Task OnConnectedAsync()
    {
        if (!await sessions.IsValidAsync(Context.User)) { Context.Abort(); return; }
        var ticket = await Context.GetHttpContext()!.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (ticket.Properties?.ExpiresUtc <= DateTimeOffset.UtcNow) { Context.Abort(); return; }
        var user = sessions.GetUserId(Context.User)!.Value;
        PresenceChange? change;
        if (distributed.Enabled)
        {
            try
            {
                var registered = await distributed.Register(Context.ConnectionId, user, ticket.Properties?.ExpiresUtc,
                    Context.Abort, Context.ConnectionAborted);
                if (!registered.Success) { Context.Abort(); return; }
                connections.Connected(Context.ConnectionId, user, Context.Abort, ticket.Properties?.ExpiresUtc);
                change = registered.Change;
            }
            catch { Context.Abort(); return; }
        }
        else change = connections.Connected(Context.ConnectionId, user, Context.Abort, ticket.Properties?.ExpiresUtc);
        await presence.Publish(change);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var localChange = connections.Disconnected(Context.ConnectionId);
        var user = sessions.GetUserId(Context.User);
        var change = distributed.Enabled && user is not null
            ? await distributed.Remove(Context.ConnectionId, user.Value) : localChange;
        await presence.Publish(change);
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
            if (distributed.Enabled)
            {
                var membership = await db.ConversationMembers.Where(x => x.ConversationId == conversationId && x.UserId == actor)
                    .Select(x => x.MembershipId).SingleAsync(Context.ConnectionAborted);
                await distributed.Subscribe(Context.ConnectionId, conversationId, membership,
                    connections.Subscription(Context.ConnectionId, conversationId)!.Value, Context.ConnectionAborted);
            }
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
        if (distributed.Enabled) await distributed.Unsubscribe(Context.ConnectionId, conversationId, Context.ConnectionAborted);
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
        var rosterVersion = distributed.Enabled ? ReceiptSnapshot.Roster(await db.ConversationMembers
            .Where(x => x.ConversationId == conversationId).Select(x => x.MembershipId).ToListAsync(Context.ConnectionAborted)) : null;
        var pageUsers = users.Take(limit).ToArray();
        PresencePage result;
        if (distributed.Enabled)
        {
            try
            {
                var snapshot = await distributed.Snapshot(pageUsers, Context.ConnectionAborted);
                result = new(conversationId, pageUsers.Select(id => snapshot.Members[id]).ToArray(),
                    users.Count > limit ? offset + limit : null, 0, snapshot.Epoch, rosterVersion);
            }
            catch { throw new HubException("Presence unavailable; retry after recovery."); }
        }
        else result = connections.Snapshot(conversationId, pageUsers, users.Count > limit ? offset + limit : null);
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
            var localChange = connections.Disconnected(Context.ConnectionId);
            var user = sessions.GetUserId(Context.User);
            var change = distributed.Enabled && user is not null
                ? await distributed.Remove(Context.ConnectionId, user.Value) : localChange;
            await presence.Publish(change);
            throw new HubException("Authentication required.");
        }
        return sessions.GetUserId(Context.User)!.Value;
    }
}
