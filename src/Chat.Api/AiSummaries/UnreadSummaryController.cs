using System.Data;
using System.Text.Json;
using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Api.AiSummaries;

[ApiController]
[Route("api/conversations/{conversationId:guid}/unread-summary")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class UnreadSummaryController(ChatDbContext db, ConversationAuthorization authorization,
    IUnreadSummaryGenerator generator, IOptions<UnreadSummaryOptions> options, IAntiforgery antiforgery,
    ILogger<UnreadSummaryController> logger) : ControllerBase
{
    private sealed record SnapshotMessage(Guid Id, DateTime CreatedAt, Guid SenderId, string Body);

    [HttpPost]
    public async Task<IActionResult> Create(Guid conversationId, CancellationToken cancellationToken)
    {
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return Problem(statusCode: 400, title: "Invalid CSRF token."); }
        var settings = options.Value;

        Guid actor, membershipId;
        Guid? readAnchor;
        List<SnapshotMessage> snapshot;
        // Repeatable-read gives one PostgreSQL snapshot. No row lock is acquired; the transaction
        // is disposed before inference so sends, receipts and membership changes can proceed.
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken))
        {
            var access = await authorization.CheckAsync(conversationId, ConversationOperation.Read, cancellationToken: cancellationToken);
            if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
            actor = access.UserId!.Value;
            var group = await authorization.ReadableConversations().Where(x => x.Id == conversationId)
                .Select(x => x.Type).SingleAsync(cancellationToken);
            if (group != ConversationType.Group) return Problem(statusCode: 409, title: "Unread summaries are available for groups only.");
            if (!settings.Enabled) return Problem(statusCode: 503, title: "Unread summaries are disabled.");
            if (!settings.IsValid()) return Problem(statusCode: 503, title: "Unread summaries are not configured.");
            var membership = await db.ConversationMembers.AsNoTracking()
                .Where(x => x.ConversationId == conversationId && x.UserId == actor)
                .Select(x => new { x.MembershipId, x.LastReadMessageId }).SingleAsync(cancellationToken);
            membershipId = membership.MembershipId;
            readAnchor = membership.LastReadMessageId;
            DateTime? anchorTime = null;
            if (readAnchor is not null)
                anchorTime = await db.Messages.Where(x => x.Id == readAnchor.Value).Select(x => x.CreatedAt).SingleAsync(cancellationToken);
            var messages = db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId && x.SenderId != actor
                && authorization.ReadableConversations().Any(c => c.Id == x.ConversationId));
            if (anchorTime is not null)
            {
                var time = anchorTime.Value;
                var anchor = readAnchor!.Value;
                messages = messages.Where(x => x.CreatedAt > time || (x.CreatedAt == time && x.Id.CompareTo(anchor) > 0));
            }
            snapshot = await messages.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .Take(settings.MaxMessages + 1)
                .Select(x => new SnapshotMessage(x.Id, x.CreatedAt, x.SenderId, x.Body))
                .ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (snapshot.Count == 0) return NoContent();
        if (snapshot.Count > settings.MaxMessages)
            return Problem(statusCode: 422, title: "Unread backlog exceeds the configured summary limit.");
        // Stable per-request pseudonyms avoid forwarding user IDs, usernames or email-like display names.
        var senders = new Dictionary<Guid, string>();
        var prompt = snapshot.Select((message, index) =>
        {
            if (!senders.TryGetValue(message.SenderId, out var sender))
                senders[message.SenderId] = sender = $"Μέλος {senders.Count + 1}";
            return new SummaryPromptMessage(index + 1, sender, message.Body);
        }).ToArray();
        SummaryContent result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var chunks = new List<SummaryContent>();
            foreach (var chunk in prompt.Chunk(settings.ChunkSize))
            {
                var partial = await generator.GenerateAsync(chunk, timeout.Token);
                if (!SummaryContentValidation.IsValid(partial)) throw new JsonException("Invalid chunk summary.");
                chunks.Add(partial);
            }
            result = chunks.Count == 1 ? chunks[0] : await generator.SynthesizeAsync(chunks, timeout.Token);
            if (!SummaryContentValidation.IsValid(result)) throw new JsonException("Invalid final summary.");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TimeoutException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning("Unread summary generation failed for conversation {ConversationId}: {ErrorType}", conversationId, error.GetType().Name);
            return Problem(statusCode: 503, title: "Unread summary is temporarily unavailable.");
        }
        // The same current-member authorization is required after slow inference. A rejoin has a
        // different MembershipId and must not receive a result from the previous incarnation.
        var current = await authorization.CheckAsync(conversationId, ConversationOperation.Read, cancellationToken: cancellationToken);
        if (!current.Allowed) return Problem(statusCode: current.ErrorStatus, title: current.ErrorTitle);
        if (!await db.ConversationMembers.AsNoTracking().AnyAsync(x => x.ConversationId == conversationId
            && x.UserId == actor && x.MembershipId == membershipId, cancellationToken))
            return Problem(statusCode: 404, title: "Conversation not found.");
        var through = snapshot[^1];
        return Ok(new UnreadSummaryResponse(readAnchor, through.Id, through.CreatedAt, snapshot.Count,
            result.Overview, result.KeyPoints, result.ActionItems, result.UnresolvedQuestions));
    }
}
