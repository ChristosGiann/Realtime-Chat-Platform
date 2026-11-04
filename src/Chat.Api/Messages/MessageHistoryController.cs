using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messages;

public sealed class MessageHistoryRequest
{
    [Range(1, 100)] public int Limit { get; init; } = 30;
    [StringLength(2048)] public string? Cursor { get; init; }
}
public sealed record MessageSender(Guid UserId, string? DisplayName, string? AvatarUrl);
public sealed record HistoryMessage(Guid Id, Guid ClientMessageId, Guid ConversationId, Guid SenderId,
    string Body, DateTime CreatedAt, MessageSender Sender);
public sealed record MessageHistoryPage(IReadOnlyList<HistoryMessage> Items, string? NextCursor);

[ApiController]
[Route("api/conversations/{conversationId:guid}/messages")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MessageHistoryController(ChatDbContext db, ConversationAuthorization authorization,
    IDataProtectionProvider protection) : ControllerBase
{
    private sealed record Position(DateTime CreatedAt, Guid Id);

    [HttpGet]
    public async Task<IActionResult> List(Guid conversationId, [FromQuery] MessageHistoryRequest request, CancellationToken cancellationToken)
    {
        var access = await authorization.CheckAsync(conversationId, ConversationOperation.Read, cancellationToken: cancellationToken);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        var protector = protection.CreateProtector("MessageHistory.v1", conversationId.ToString(), access.UserId!.Value.ToString(), request.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        IQueryable<Message> messages = db.Messages.AsNoTracking();
        if (request.Cursor is not null)
        {
            Position? position;
            try { position = JsonSerializer.Deserialize<Position>(protector.Unprotect(request.Cursor)); }
            catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
            { return Problem(statusCode: 400, title: "Invalid history cursor."); }
            if (position is null || position.Id == Guid.Empty || position.CreatedAt.Kind != DateTimeKind.Utc)
                return Problem(statusCode: 400, title: "Invalid history cursor.");
            // PostgreSQL tuple comparison matches the composite history index, including UUID ordering.
            messages = db.Messages.FromSqlInterpolated($"SELECT * FROM messages.\"Messages\" WHERE \"ConversationId\" = {conversationId} AND (\"CreatedAt\", \"Id\") < ({position.CreatedAt}, {position.Id})").AsNoTracking();
        }
        var items = await messages.Where(x => x.ConversationId == conversationId &&
                authorization.ReadableConversations().Any(c => c.Id == x.ConversationId))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(request.Limit + 1)
            .Select(x => new HistoryMessage(x.Id, x.ClientMessageId, x.ConversationId, x.SenderId, x.Body, x.CreatedAt,
                new MessageSender(x.SenderId, db.Profiles.Where(p => p.UserId == x.SenderId).Select(p => p.DisplayName).FirstOrDefault(),
                    db.Profiles.Where(p => p.UserId == x.SenderId).Select(p => p.AvatarUrl).FirstOrDefault())))
            .ToListAsync(cancellationToken);
        var page = items.Take(request.Limit).ToArray();
        var next = items.Count > request.Limit
            ? protector.Protect(JsonSerializer.Serialize(new Position(page[^1].CreatedAt, page[^1].Id))) : null;
        return Ok(new MessageHistoryPage(page, next));
    }
}
