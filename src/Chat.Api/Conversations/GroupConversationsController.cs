using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Conversations;

[ApiController]
[Route("api/conversations/group")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GroupConversationsController(ChatDbContext db, ICurrentUser currentUser, IAntiforgery antiforgery) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<GroupConversationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(CreateGroupConversationRequest request, CancellationToken cancellationToken)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }

        var userId = currentUser.UserId;
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId.Value, cancellationToken))
            return Problem(statusCode: 401, title: "Authentication required.");

        var memberIds = request.MemberIds!.Append(userId.Value).Distinct().ToArray();
        if (await db.Users.CountAsync(x => memberIds.Contains(x.Id), cancellationToken) != memberIds.Length)
            return MissingMember();

        var conversation = new Conversation { Type = ConversationType.Group, Name = request.Name!.Trim() };
        foreach (var id in memberIds)
            conversation.Members.Add(new ConversationMember
            {
                UserId = id,
                Role = id == userId.Value ? ConversationMemberRole.Owner : ConversationMemberRole.Member
            });
        db.Conversations.Add(conversation);
        try
        {
            // One transaction persists the group, its owner and all requested members.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ForeignKeyViolation, ConstraintName: "FK_ConversationMembers_AspNetUsers_UserId" })
        {
            db.ChangeTracker.Clear();
            if (!await db.Users.AnyAsync(x => x.Id == userId.Value, cancellationToken))
                return Problem(statusCode: 401, title: "Authentication required.");
            return MissingMember();
        }

        return StatusCode(StatusCodes.Status201Created, new GroupConversationResponse(
            conversation.Id, "Group", conversation.Name, conversation.CreatedAt,
            conversation.Members.OrderBy(x => x.UserId)
                .Select(x => new GroupMemberResponse(x.UserId, x.Role.ToString(), x.JoinedAt)).ToArray()));
    }

    private ObjectResult MissingMember() => Problem(statusCode: 404, title: "One or more users were not found.");
}
