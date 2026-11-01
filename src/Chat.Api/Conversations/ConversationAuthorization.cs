using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Conversations;

public enum ConversationOperation { Read, AddMember, RemoveMember, Leave }

public sealed record ConversationAccess(Guid? UserId, int? ErrorStatus = null, string? ErrorTitle = null)
{
    public bool Allowed => ErrorStatus is null;
}

/// <summary>Request-scoped authorization based on current Identity and PostgreSQL membership.</summary>
public sealed class ConversationAuthorization(ChatDbContext db, ICurrentUser currentUser)
{
    // Compose protected projections on this query so membership is checked in the same SQL read.
    public IQueryable<Conversation> ReadableConversations()
    {
        var userId = currentUser.UserId;
        if (userId is null) return db.Conversations.AsNoTracking().Where(_ => false);
        var actor = userId.Value;
        return db.Conversations.AsNoTracking().Where(c =>
            db.Users.Any(u => u.Id == actor) && c.Members.Any(m => m.UserId == actor &&
                ((c.Type == ConversationType.Group && (m.Role == ConversationMemberRole.Owner || m.Role == ConversationMemberRole.Member)) ||
                 (c.Type == ConversationType.Direct && m.Role == ConversationMemberRole.Member))));
    }

    // Mutation callers must hold the conversation row lock until the write commits.
    public async Task<ConversationAccess> CheckAsync(Guid conversationId, ConversationOperation operation,
        Guid? targetUserId = null, CancellationToken cancellationToken = default)
    {
        var actor = currentUser.UserId;
        if (actor is null || !await db.Users.AnyAsync(x => x.Id == actor.Value, cancellationToken))
            return new(null, 401, "Authentication required.");
        var conversation = await ReadableConversations().Where(x => x.Id == conversationId)
            .Select(x => new { x.Type, Role = x.Members.Where(m => m.UserId == actor.Value).Select(m => m.Role).Single() })
            .SingleOrDefaultAsync(cancellationToken);
        if (conversation is null) return new(actor, 404, "Conversation not found.");
        if (operation == ConversationOperation.Read) return new(actor);
        if (conversation.Type != ConversationType.Group)
            return new(actor, 409, "Direct conversation membership cannot be changed.");
        if (operation == ConversationOperation.Leave)
            return conversation.Role == ConversationMemberRole.Owner
                ? new(actor, 409, "The group owner cannot leave or be removed.") : new(actor);
        if (operation is not (ConversationOperation.AddMember or ConversationOperation.RemoveMember))
            return new(actor, 403, "Operation not permitted.");
        if (conversation.Role != ConversationMemberRole.Owner)
            return new(actor, 403, "Only the group owner can manage members.");
        if (targetUserId is null || targetUserId == Guid.Empty)
            return new(actor, 400, "User ID must not be empty.");
        if (operation == ConversationOperation.RemoveMember && await db.ConversationMembers.AnyAsync(
            x => x.ConversationId == conversationId && x.UserId == targetUserId.Value && x.Role == ConversationMemberRole.Owner, cancellationToken))
            return new(actor, 409, "The group owner cannot leave or be removed.");
        return new(actor);
    }
}
