using System.ComponentModel.DataAnnotations;
using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messages;

[ApiController]
[Route("api/conversations/{conversationId:guid}/receipts")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ReceiptsController(ChatDbContext db, ConversationAuthorization authorization) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(Guid conversationId, [Range(0, int.MaxValue)] int offset = 0,
        [Range(1, 100)] int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conversation = await db.Conversations.FromSqlInterpolated(
            $"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversationId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        var access = await authorization.CheckAsync(conversationId, ConversationOperation.Read, cancellationToken: cancellationToken);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        var members = await db.ConversationMembers.Where(x => x.ConversationId == conversationId)
            .Select(x => new { x.UserId, x.MembershipId }).ToListAsync(cancellationToken);
        var items = await ReceiptSnapshot.Members(db, conversationId, offset, limit, cancellationToken);
        var result = new ReceiptPage(conversationId, conversation!.Type.ToString(), ReceiptSnapshot.Roster(members.Select(x => x.MembershipId)),
            members.Single(x => x.UserId == access.UserId).MembershipId, items,
            (long)offset + items.Count < members.Count ? offset + items.Count : null);
        await transaction.CommitAsync(cancellationToken);
        return Ok(result);
    }
}
