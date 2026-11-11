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
public sealed class AdvanceDeliveryRequest : IValidatableObject
{
    [Required] public Guid? MessageId { get; init; }
    [Required] public Guid? MembershipId { get; init; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MessageId == Guid.Empty) yield return new("Message identifier must not be empty.", [nameof(MessageId)]);
        if (MembershipId == Guid.Empty) yield return new("Membership identifier must not be empty.", [nameof(MembershipId)]);
    }
}
public sealed record DeliveryStateResponse(Guid MembershipId, Guid? LastDeliveredMessageId, DateTime? LastDeliveredAt);

[ApiController]
[Route("api/conversations/{conversationId:guid}/delivery-state")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DeliveryStateController(ChatDbContext db, ConversationAuthorization authorization, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Get(Guid conversationId, CancellationToken token) => Execute(conversationId, null, token);

    [HttpPut]
    public async Task<IActionResult> Advance(Guid conversationId, AdvanceDeliveryRequest request, CancellationToken token)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }
        return await Execute(conversationId, request, token);
    }
    private async Task<IActionResult> Execute(Guid conversation, AdvanceDeliveryRequest? request, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleOrDefaultAsync(token);
        var access = await authorization.CheckAsync(conversation, ConversationOperation.Read, cancellationToken: token);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        var actor = access.UserId!.Value;
        var membership = await db.ConversationMembers.Where(x => x.ConversationId == conversation && x.UserId == actor).Select(x => x.MembershipId).SingleAsync(token);
        if (request is not null)
        {
            if (membership != request.MembershipId) return Problem(statusCode: 409, title: "Membership changed. Refresh conversation state.");
            if (!await db.Messages.AnyAsync(x => x.ConversationId == conversation && x.Id == request.MessageId, token))
                return Problem(statusCode: 404, title: "Message not found.");
            await ReceiptProgress.Advance(db, conversation, actor, request.MessageId!.Value, false, token);
        }
        var state = await db.Database.SqlQuery<DeliveryStateResponse>($"""
            SELECT member."MembershipId", member."LastDeliveredMessageId", anchor."CreatedAt" AS "LastDeliveredAt"
            FROM conversations."ConversationMembers" AS member
            LEFT JOIN messages."Messages" AS anchor ON anchor."Id" = member."LastDeliveredMessageId"
            WHERE member."ConversationId" = {conversation} AND member."UserId" = {actor}
            """).SingleAsync(token);
        await transaction.CommitAsync(token);
        return Ok(state);
    }
}
