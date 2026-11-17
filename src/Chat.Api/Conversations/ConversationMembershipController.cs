using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Realtime;
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
public sealed class ConversationMembershipController(ChatDbContext db, ConversationAuthorization authorization, IAntiforgery antiforgery,
    ConversationConnections connections, DistributedConnectionRegistry distributed, RevocationBarrier barrier,
    MembershipNotifications notifications, ILogger<ConversationMembershipController> logger) : ControllerBase
{
    [HttpPut("members/{userId:guid}")]
    public Task<IActionResult> Add(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        Change(conversationId, userId, ConversationOperation.AddMember, cancellationToken);

    [HttpDelete("members/{userId:guid}")]
    public Task<IActionResult> Remove(Guid conversationId, Guid userId, CancellationToken cancellationToken) =>
        Change(conversationId, userId, ConversationOperation.RemoveMember, cancellationToken);

    [HttpDelete("membership")]
    public Task<IActionResult> Leave(Guid conversationId, CancellationToken cancellationToken) =>
        Change(conversationId, null, ConversationOperation.Leave, cancellationToken);

    private async Task<IActionResult> Change(Guid conversationId, Guid? targetId, ConversationOperation operation, CancellationToken cancellationToken)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        using var lockMeasurement = new ConversationLockMeasurement("membership.change");
        // Serialize membership changes for this conversation before reading permissions or members.
        await db.Conversations.FromSqlInterpolated(
            $"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        lockMeasurement.Acquired();
        var access = await authorization.CheckAsync(conversationId, operation, targetId, cancellationToken);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        var target = operation == ConversationOperation.Leave ? access.UserId!.Value : targetId!.Value;
        var member = await db.ConversationMembers.SingleOrDefaultAsync(
            x => x.ConversationId == conversationId && x.UserId == target, cancellationToken);
        if (operation == ConversationOperation.AddMember)
        {
            if (member is not null) return NoContent();
            if (!await db.Users.AnyAsync(x => x.Id == target, cancellationToken))
                return Problem(statusCode: 404, title: "User not found.");
            db.ConversationMembers.Add(new ConversationMember { ConversationId = conversationId, UserId = target });
        }
        else
        {
            if (member is null) return NoContent();
            db.ConversationMembers.Remove(member);
        }
        using var barrierDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (member is not null && operation != ConversationOperation.AddMember && distributed.Enabled)
            barrierDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        DistributedConnectionRegistry.RevocationSnapshot? snapshot = null;
        if (member is not null && operation != ConversationOperation.AddMember && distributed.Enabled)
        {
            try { snapshot = await distributed.SnapshotRevocation(conversationId, target, member.MembershipId, barrierDeadline.Token); }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Revocation owner snapshot unavailable; membership unchanged.");
                await transaction.RollbackAsync(CancellationToken.None);
                return Problem(statusCode: 503, title: "Revocation coordination unavailable; retry.");
            }
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            // Fail closed before releasing the row lock: a later broadcast cannot overtake revocation.
            if (operation != ConversationOperation.AddMember)
            {
                if (snapshot is not null) await barrier.Revoke(snapshot, conversationId, target, member!.MembershipId, barrierDeadline.Token);
                else await connections.Revoke(conversationId, target, cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "FK_ConversationMembers_AspNetUsers_UserId" })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return Problem(statusCode: 404, title: "User not found.");
        }
        catch (Exception exception) when (snapshot is not null && exception is not DbUpdateException
            && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(exception, "Distributed revocation barrier failed; membership rolled back.");
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return Problem(statusCode: 503, title: "Revocation coordination unavailable; retry.");
        }
        // A commit failure after valid ACKs must never restore old subscription generations.
        await transaction.CommitAsync(cancellationToken);
        lockMeasurement.Dispose();
        await PostCommitNotification.Send(token => notifications.Changed(conversationId, target,
            operation == ConversationOperation.AddMember ? null : member!.MembershipId, token), logger);
        return NoContent();
    }

}
