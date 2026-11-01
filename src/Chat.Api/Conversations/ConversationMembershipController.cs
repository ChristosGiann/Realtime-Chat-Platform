using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Conversations;

[ApiController]
[Route("api/conversations/{conversationId:guid}")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ConversationMembershipController(ChatDbContext db, ICurrentUser currentUser, IAntiforgery antiforgery) : ControllerBase
{
    [HttpPut("members/{userId:guid}")]
    public Task<IActionResult> Add(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        Change(conversationId, userId, MembershipChange.Add, cancellationToken);

    [HttpDelete("members/{userId:guid}")]
    public Task<IActionResult> Remove(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        Change(conversationId, userId, MembershipChange.Remove, cancellationToken);

    [HttpDelete("membership")]
    public Task<IActionResult> Leave(Guid conversationId, CancellationToken cancellationToken) =>
        Change(conversationId, null, MembershipChange.Leave, cancellationToken);

    private async Task<IActionResult> Change(Guid conversationId, Guid? targetId, MembershipChange operation, CancellationToken cancellationToken)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }
        var actor = currentUser.UserId;
        if (actor is null || !await db.Users.AnyAsync(x => x.Id == actor.Value, cancellationToken))
            return Problem(statusCode: 401, title: "Authentication required.");
        if (targetId == Guid.Empty) return Problem(statusCode: 400, title: "User ID must not be empty.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize membership changes for this conversation before reading permissions or members.
        var conversation = await db.Conversations.FromSqlInterpolated(
            $"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (conversation is null) return Missing();
        var membership = await db.ConversationMembers.SingleOrDefaultAsync(
            x => x.ConversationId == conversationId && x.UserId == actor.Value, cancellationToken);
        if (membership is null) return Missing();
        if (conversation.Type != ConversationType.Group)
            return Problem(statusCode: 409, title: "Direct conversation membership cannot be changed.");
        if (operation != MembershipChange.Leave && membership.Role != ConversationMemberRole.Owner)
            return Problem(statusCode: 403, title: "Only the group owner can manage members.");

        var target = operation == MembershipChange.Leave ? actor.Value : targetId!.Value;
        var member = await db.ConversationMembers.SingleOrDefaultAsync(
            x => x.ConversationId == conversationId && x.UserId == target, cancellationToken);
        if (operation == MembershipChange.Add)
        {
            if (member is not null) return NoContent();
            if (!await db.Users.AnyAsync(x => x.Id == target, cancellationToken))
                return Problem(statusCode: 404, title: "User not found.");
            db.ConversationMembers.Add(new ConversationMember { ConversationId = conversationId, UserId = target });
        }
        else
        {
            if (member is null) return NoContent();
            if (member.Role == ConversationMemberRole.Owner)
                return Problem(statusCode: 409, title: "The group owner cannot leave or be removed.");
            db.ConversationMembers.Remove(member);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "FK_ConversationMembers_AspNetUsers_UserId" })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return Problem(statusCode: 404, title: "User not found.");
        }
        return NoContent();
    }

    private ObjectResult Missing() => Problem(statusCode: 404, title: "Conversation not found.");
    private enum MembershipChange { Add, Remove, Leave }
}
