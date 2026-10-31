using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Conversations;

[ApiController]
[Route("api/conversations/direct")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DirectConversationsController(ChatDbContext db, ICurrentUser currentUser, IAntiforgery antiforgery) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<DirectConversationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<DirectConversationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateDirectConversationRequest request, CancellationToken cancellationToken)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }

        var userId = currentUser.UserId;
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId.Value, cancellationToken))
            return Problem(statusCode: 401, title: "Authentication required.");
        var targetId = request.TargetUserId!.Value;
        if (targetId == userId.Value)
        {
            ModelState.AddModelError(nameof(request.TargetUserId), "Cannot create a direct conversation with yourself.");
            return ValidationProblem(ModelState);
        }
        if (!await db.Users.AnyAsync(x => x.Id == targetId, cancellationToken)) return MissingUser();
        var low = userId.Value.CompareTo(targetId) < 0 ? userId.Value : targetId;
        var high = userId.Value.CompareTo(targetId) < 0 ? targetId : userId.Value;
        var existing = await FindPair(low, high, cancellationToken);
        if (existing is not null) return ExistingResponse(existing, low, high);

        var conversation = new Conversation { Type = ConversationType.Direct, DirectUserLowId = low, DirectUserHighId = high };
        conversation.Members.Add(new ConversationMember { UserId = low, Role = ConversationMemberRole.Member });
        conversation.Members.Add(new ConversationMember { UserId = high, Role = ConversationMemberRole.Member });
        db.Conversations.Add(conversation);
        try
        {
            // One SaveChanges transaction inserts the conversation and both memberships, or nothing.
            await db.SaveChangesAsync(cancellationToken);
            return StatusCode(StatusCodes.Status201Created, ToResponse(conversation));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_Conversations_DirectPair" })
        {
            // PostgreSQL waits for the competing transaction; the failed write has been rolled back.
            db.ChangeTracker.Clear();
            existing = await FindPair(low, high, cancellationToken);
            return existing is null ? RetryConflict() : ExistingResponse(existing, low, high);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation } postgres &&
            postgres.ConstraintName is "FK_Conversations_AspNetUsers_DirectUserLowId" or
                "FK_Conversations_AspNetUsers_DirectUserHighId" or "FK_ConversationMembers_AspNetUsers_UserId")
        {
            db.ChangeTracker.Clear();
            if (!await db.Users.AnyAsync(x => x.Id == userId.Value, cancellationToken))
                return Problem(statusCode: 401, title: "Authentication required.");
            return !await db.Users.AnyAsync(x => x.Id == targetId, cancellationToken) ? MissingUser() : RetryConflict();
        }
    }

    private Task<Conversation?> FindPair(Guid low, Guid high, CancellationToken cancellationToken) =>
        db.Conversations.AsNoTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Type == ConversationType.Direct && x.DirectUserLowId == low && x.DirectUserHighId == high, cancellationToken);

    private IActionResult ExistingResponse(Conversation conversation, Guid low, Guid high)
    {
        // Do not expose or silently repair inconsistent rows inserted outside this creation flow.
        if (conversation.Members.Count != 2 || conversation.Members.Any(x => x.Role != ConversationMemberRole.Member) ||
            !conversation.Members.Any(x => x.UserId == low) || !conversation.Members.Any(x => x.UserId == high))
            return Problem(statusCode: 409, title: "Conversation membership is inconsistent.");
        return Ok(ToResponse(conversation));
    }
    private static DirectConversationResponse ToResponse(Conversation conversation) =>
        new(conversation.Id, "Direct", conversation.CreatedAt, conversation.Members.Select(x => x.UserId).Order().ToArray());
    private ObjectResult MissingUser() => Problem(statusCode: 404, title: "User not found.");
    private ObjectResult RetryConflict() => Problem(statusCode: 409, title: "Conversation changed. Retry the request.");
}
