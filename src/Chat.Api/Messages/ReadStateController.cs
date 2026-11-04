using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AdvanceReadRequest : IValidatableObject
{
    [Required] public Guid? MessageId { get; init; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MessageId == Guid.Empty) yield return new ValidationResult("Message identifier must not be empty.", [nameof(MessageId)]);
    }
}
public sealed record ReadStateResponse(Guid? LastReadMessageId, DateTime? LastReadAt, long UnreadCount);

[ApiController]
[Route("api/conversations/{conversationId:guid}/read-state")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReadStateController(ChatDbContext db, ConversationAuthorization authorization, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(Guid conversationId, CancellationToken cancellationToken) => Execute(conversationId, null, cancellationToken);

    [HttpPut]
    public async Task<IActionResult> Advance(Guid conversationId, AdvanceReadRequest request, CancellationToken cancellationToken)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }
        return await Execute(conversationId, request.MessageId!.Value, cancellationToken);
    }

    private async Task<IActionResult> Execute(Guid conversationId, Guid? messageId, CancellationToken cancellationToken)
    {
        // Same ordering as send/membership mutations, including authorization and the response snapshot.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var access = await authorization.CheckAsync(conversationId, ConversationOperation.Read, cancellationToken: cancellationToken);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        var actor = access.UserId!.Value;
        if (messageId is not null)
        {
            if (!await db.Messages.AnyAsync(x => x.ConversationId == conversationId && x.Id == messageId, cancellationToken))
                return Problem(statusCode: 404, title: "Message not found.");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE conversations."ConversationMembers" AS member SET "LastReadMessageId" = {messageId.Value}
                FROM messages."Messages" AS target
                WHERE member."ConversationId" = {conversationId} AND member."UserId" = {actor}
                    AND target."ConversationId" = member."ConversationId" AND target."Id" = {messageId.Value}
                    AND (member."LastReadMessageId" IS NULL OR EXISTS (
                        SELECT 1 FROM messages."Messages" AS previous WHERE previous."Id" = member."LastReadMessageId"
                        AND (target."CreatedAt", target."Id") > (previous."CreatedAt", previous."Id")))
                """, cancellationToken);
        }
        var state = await db.Database.SqlQuery<ReadStateResponse>($"""
            SELECT member."LastReadMessageId", anchor."CreatedAt" AS "LastReadAt",
                (SELECT count(*) FROM messages."Messages" AS message
                 WHERE message."ConversationId" = member."ConversationId" AND message."SenderId" <> {actor}
                   AND (anchor."Id" IS NULL OR (message."CreatedAt", message."Id") > (anchor."CreatedAt", anchor."Id"))) AS "UnreadCount"
            FROM conversations."ConversationMembers" AS member
            LEFT JOIN messages."Messages" AS anchor ON anchor."Id" = member."LastReadMessageId"
            WHERE member."ConversationId" = {conversationId} AND member."UserId" = {actor}
            """).SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(state);
    }
}
