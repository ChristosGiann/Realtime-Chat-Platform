using System.ComponentModel.DataAnnotations;
using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Conversations;

public sealed class ConversationPageRequest
{
    [Range(0, int.MaxValue - 50)] public int Offset { get; init; }
    [Range(1, 50)] public int Limit { get; init; } = 20;
}
public sealed record ConversationSummary(Guid Id, string Type, string? Name, DateTime CreatedAt, string MyRole, int MemberCount);
public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, int? NextOffset);
public sealed record ConversationMemberSummary(Guid UserId, string Role, DateTime JoinedAt, PublicProfileResponse? Profile);
public sealed record ConversationDetails(Guid Id, string Type, string? Name, DateTime CreatedAt, string MyRole,
    int MemberCount, IReadOnlyList<ConversationMemberSummary> Members, int? NextOffset);

[ApiController]
[Route("api/conversations")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ConversationQueriesController(ChatDbContext db, ConversationAuthorization authorization, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ConversationPageRequest page, CancellationToken cancellationToken)
    {
        var actor = currentUser.UserId;
        if (actor is null || !await db.Users.AnyAsync(x => x.Id == actor.Value, cancellationToken))
            return Problem(statusCode: 401, title: "Authentication required.");
        var items = await authorization.ReadableConversations().OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip(page.Offset).Take(page.Limit + 1)
            .Select(x => new ConversationSummary(x.Id, x.Type == ConversationType.Direct ? "Direct" : "Group", x.Name, x.CreatedAt,
                x.Members.Where(m => m.UserId == actor.Value).Select(m => m.Role == ConversationMemberRole.Owner ? "Owner" : "Member").Single(),
                x.Members.Count)).ToListAsync(cancellationToken);
        return Ok(new ConversationPage(items.Take(page.Limit).ToArray(), items.Count > page.Limit ? page.Offset + page.Limit : null));
    }

    [HttpGet("{conversationId:guid}")]
    public async Task<IActionResult> Details(Guid conversationId, [FromQuery] ConversationPageRequest page, CancellationToken cancellationToken)
    {
        var access = await authorization.CheckAsync(conversationId, ConversationOperation.Read, cancellationToken: cancellationToken);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        // Membership remains part of this SQL read even if it changed after CheckAsync.
        var details = await authorization.ReadableConversations().Where(x => x.Id == conversationId)
            .Select(x => new ConversationDetails(x.Id, x.Type == ConversationType.Direct ? "Direct" : "Group", x.Name, x.CreatedAt,
                x.Members.Where(m => m.UserId == access.UserId!.Value).Select(m => m.Role == ConversationMemberRole.Owner ? "Owner" : "Member").Single(),
                x.Members.Count, x.Members.OrderBy(m => m.UserId).Skip(page.Offset).Take(page.Limit + 1)
                    .Select(m => new ConversationMemberSummary(m.UserId, m.Role == ConversationMemberRole.Owner ? "Owner" : "Member", m.JoinedAt,
                        db.Profiles.Where(p => p.UserId == m.UserId).Select(p => new PublicProfileResponse(p.UserId, p.DisplayName, p.Bio, p.AvatarUrl)).FirstOrDefault()))
                    .ToList(), null)).SingleOrDefaultAsync(cancellationToken);
        if (details is null) return Problem(statusCode: 404, title: "Conversation not found.");
        return Ok(details with { Members = details.Members.Take(page.Limit).ToArray(), NextOffset = details.Members.Count > page.Limit ? page.Offset + page.Limit : null });
    }
}
