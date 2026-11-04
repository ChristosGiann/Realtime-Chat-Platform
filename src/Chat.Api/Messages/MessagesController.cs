using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chat.Api.Messages;

[ApiController]
[Route("api/conversations/{conversationId:guid}/messages")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MessagesController(ChatDbContext db, ConversationAuthorization authorization, IAntiforgery antiforgery) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
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
        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(x => x.ClientMessageId == request.ClientMessageId, cancellationToken);
        if (existing is not null)
        {
            if (existing.ConversationId != conversationId || existing.SenderId != access.UserId || existing.Body != request.Body)
                return KeyConflict();
            return Ok(ToResponse(existing));
        }
        var message = new Message { ClientMessageId = request.ClientMessageId!.Value, ConversationId = conversationId, SenderId = access.UserId!.Value, Body = request.Body! };
        db.Messages.Add(message);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_Messages_ClientMessageId" })
        {
            // Different conversations have independent locks; PostgreSQL arbitrates key collisions.
            await transaction.RollbackAsync(cancellationToken);
            return KeyConflict();
        }
        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            ToResponse(message));
    }

    private ObjectResult KeyConflict() => Problem(statusCode: 409, title: "Client message identifier is already bound to another request.");
    private static MessageResponse ToResponse(Message message) =>
        new(message.Id, message.ClientMessageId, message.ConversationId, message.SenderId, message.Body, message.CreatedAt);
}
