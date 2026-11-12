using Chat.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Messages;

public static class ReceiptProgress
{
    // Caller holds the conversation row lock and transaction through authorization, both updates and commit.
    public static async Task<bool> Advance(ChatDbContext db, Guid conversation, Guid actor, Guid message, bool read, CancellationToken token)
    {
        var changed = 0;
        if (read)
            changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE conversations."ConversationMembers" AS member SET "LastReadMessageId" = {message}
                FROM messages."Messages" AS target
                WHERE member."ConversationId" = {conversation} AND member."UserId" = {actor}
                    AND target."ConversationId" = member."ConversationId" AND target."Id" = {message}
                    AND (member."LastReadMessageId" IS NULL OR EXISTS (
                        SELECT 1 FROM messages."Messages" AS previous WHERE previous."Id" = member."LastReadMessageId"
                        AND (target."CreatedAt", target."Id") > (previous."CreatedAt", previous."Id")))
                """, token);
        // Read is a durable observation assertion. Its anchor is a floor even for a legacy read write.
        changed += await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE conversations."ConversationMembers" AS member SET "LastDeliveredMessageId" = target."Id"
            FROM (
                SELECT candidate."Id", candidate."CreatedAt" FROM messages."Messages" AS candidate
                WHERE candidate."ConversationId" = {conversation} AND (candidate."Id" = {message} OR candidate."Id" = (
                    SELECT "LastReadMessageId" FROM conversations."ConversationMembers"
                    WHERE "ConversationId" = {conversation} AND "UserId" = {actor}))
                ORDER BY candidate."CreatedAt" DESC, candidate."Id" DESC LIMIT 1
            ) AS target
            WHERE member."ConversationId" = {conversation} AND member."UserId" = {actor}
                AND (member."LastDeliveredMessageId" IS NULL OR EXISTS (
                    SELECT 1 FROM messages."Messages" AS previous WHERE previous."Id" = member."LastDeliveredMessageId"
                    AND (target."CreatedAt", target."Id") > (previous."CreatedAt", previous."Id")))
            """, token);
        return changed > 0;
    }
}
