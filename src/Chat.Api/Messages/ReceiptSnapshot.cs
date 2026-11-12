using System.Security.Cryptography;
using System.Text;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messages;

public sealed record ReceiptAnchor(Guid MessageId, DateTime CreatedAt);
public sealed record MemberReceipt(Guid UserId, Guid MembershipId, ReceiptAnchor? Delivered, ReceiptAnchor? Read);
public sealed record ReceiptPage(Guid ConversationId, string ConversationType, string RosterVersion,
    Guid MyMembershipId, IReadOnlyList<MemberReceipt> Items, int? NextOffset);

public static class ReceiptSnapshot
{
    // Caller holds the conversation lock. Hash identifies the cohort, not progress or event order.
    public static string Roster(IEnumerable<Guid> memberships) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join(',', memberships.Select(x => x.ToString("D")).Order(StringComparer.Ordinal)))));

    public static async Task<List<MemberReceipt>> Members(ChatDbContext db, Guid conversation, int offset, int limit, CancellationToken token)
    {
        var rows = await (from member in db.ConversationMembers.AsNoTracking()
            where member.ConversationId == conversation
            join delivered in db.Messages on member.LastDeliveredMessageId equals delivered.Id into deliveries
            from delivered in deliveries.DefaultIfEmpty()
            join read in db.Messages on member.LastReadMessageId equals read.Id into reads
            from read in reads.DefaultIfEmpty()
            orderby member.UserId
            select new { member.UserId, member.MembershipId, DeliveryId = (Guid?)delivered.Id,
                DeliveryAt = (DateTime?)delivered.CreatedAt, ReadId = (Guid?)read.Id, ReadAt = (DateTime?)read.CreatedAt })
            .Skip(offset).Take(limit).ToListAsync(token);
        return rows.Select(x => new MemberReceipt(x.UserId, x.MembershipId,
            x.DeliveryId is { } d ? new(d, x.DeliveryAt!.Value) : null,
            x.ReadId is { } r ? new(r, x.ReadAt!.Value) : null)).ToList();
    }
}
