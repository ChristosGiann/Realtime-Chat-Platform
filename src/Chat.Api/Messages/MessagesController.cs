using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messages;

[ApiController]
[Route("api/conversations/{conversationId:guid}/messages")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MessagesController(ChatDbContext db, ConversationAuthorization authorization, IAntiforgery antiforgery) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(Guid conversationId, CreateMessageRequest request, CancellationToken cancellationToken)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Conversations.FromSqlInterpolated(
            $"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var access = await authorization.CheckAsync(conversationId, ConversationOperation.SendMessage, cancellationToken: cancellationToken);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        var message = new Message { ConversationId = conversationId, SenderId = access.UserId!.Value, Body = request.Body! };
        db.Messages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            new MessageResponse(message.Id, message.ConversationId, message.SenderId, message.Body, message.CreatedAt));
    }
}
