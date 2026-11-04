using Chat.Api.Conversations;
using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Messages;
using Chat.Api.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Chat.Api.IntegrationTests;

public sealed class MessagePersistenceTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private IServiceScope Scope() => fixture.Factory.Services.CreateScope();
    private static ChatDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    internal static async Task<(Guid User, Guid Conversation)> Seed(ChatDbContext db)
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, UserName = id.ToString("N"), NormalizedUserName = id.ToString("N").ToUpperInvariant(),
            Email = $"{id:N}@example.test", NormalizedEmail = $"{id:N}@example.test".ToUpperInvariant() });
        db.Profiles.Add(new UserProfile { UserId = id, DisplayName = "Sender" });
        var group = new Conversation { Type = ConversationType.Group, Name = "Messages" };
        group.Members.Add(new ConversationMember { UserId = id, Role = ConversationMemberRole.Owner });
        db.Conversations.Add(group); await db.SaveChangesAsync();
        return (id, group.Id);
    }
    private static async Task Reject(ChatDbContext db, string code)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(code, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task FreshMigration_PersistsStableIdsUtcAndBody_AndHistoryIndexOrdersTies()
    {
        using var scope = Scope(); var db = Db(scope); var seed = await Seed(db);
        var timestamp = new DateTime(2026, 11, 3, 1, 0, 0, DateTimeKind.Utc);
        var messages = Enumerable.Range(0, 3).Select(_ => new Message { ConversationId = seed.Conversation, SenderId = seed.User,
            Body = "  Ελληνικά\n<script>text</script> 😀  ", CreatedAt = timestamp }).ToArray();
        db.Messages.AddRange(messages);
        db.Messages.Add(new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "v" });
        var boundary = new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = new string('x', Message.MaxBodyLength) };
        db.Messages.Add(boundary); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Pacific/Honolulu'");
        var saved = await db.Messages.SingleAsync(x => x.Id == boundary.Id);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind); Assert.NotEqual(default, saved.CreatedAt);
        Assert.Equal(Message.MaxBodyLength, saved.Body.Length);
        var rows = await db.Messages.Where(x => x.ConversationId == seed.Conversation && x.CreatedAt == timestamp)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync();
        Assert.Equal(messages.Select(x => x.Id).OrderDescending(), rows.Select(x => x.Id));
        Assert.All(rows, x => Assert.Equal(messages[0].Body, x.Body));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        var index = await db.Database.SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'messages' AND indexname = 'IX_Messages_ConversationHistory'").SingleAsync();
        Assert.Contains("\"ConversationId\", \"CreatedAt\" DESC, \"Id\" DESC", index);
        await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'UTC'");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n\r")]
    [InlineData("\v")]
    [InlineData("\u00a0\u2003\u202f\u3000")]
    public async Task DatabaseRejectsEmptyOrWhitespaceBody(string body)
    {
        using var scope = Scope(); var db = Db(scope); var seed = await Seed(db);
        db.Messages.Add(new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = body });
        await Reject(db, PostgresErrorCodes.CheckViolation);
    }

    [Theory]
    [InlineData("null", PostgresErrorCodes.NotNullViolation)]
    [InlineData("long", PostgresErrorCodes.StringDataRightTruncation)]
    [InlineData("emptyId", PostgresErrorCodes.CheckViolation)]
    [InlineData("sender", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("conversation", PostgresErrorCodes.ForeignKeyViolation)]
    [InlineData("infinity", PostgresErrorCodes.CheckViolation)]
    public async Task DatabaseRejectsInvalidMessage(string scenario, string code)
    {
        using var scope = Scope(); var db = Db(scope); var seed = await Seed(db);
        var message = new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "Valid" };
        switch (scenario)
        {
            case "null": message.Body = null!; break;
            case "long": message.Body = new string('x', Message.MaxBodyLength + 1); break;
            case "emptyId": message.Id = Guid.Empty; break;
            case "sender": message.SenderId = Guid.NewGuid(); break;
            case "conversation": message.ConversationId = Guid.NewGuid(); break;
            case "infinity": message.CreatedAt = DateTime.MaxValue; break;
        }
        db.Messages.Add(message); await Reject(db, code);
    }

    [Fact]
    public async Task DuplicateIdAndNonUtcWritesAreRejected()
    {
        using var scope = Scope(); var db = Db(scope); var seed = await Seed(db);
        var message = new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "Saved" };
        db.Messages.Add(message); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        db.Messages.Add(new Message { Id = message.Id, ConversationId = seed.Conversation, SenderId = seed.User, Body = "Duplicate" });
        await Reject(db, PostgresErrorCodes.UniqueViolation); db.ChangeTracker.Clear();
        db.Messages.Add(new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "Invalid time", CreatedAt = new DateTime(2026, 1, 1) });
        Assert.Contains("UTC", (await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync())).ToString());
    }

    [Fact]
    public async Task MembershipRemovalPreservesHistory_AndUserConversationDeletionIsRestricted()
    {
        using var scope = Scope(); var db = Db(scope); var seed = await Seed(db);
        db.Messages.Add(new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "Retained" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.ConversationMembers.Where(x => x.ConversationId == seed.Conversation).ExecuteDeleteAsync();
        Assert.Equal("Retained", (await db.Messages.AsNoTracking().SingleAsync(x => x.ConversationId == seed.Conversation)).Body);
        db.Conversations.Remove(await db.Conversations.SingleAsync(x => x.Id == seed.Conversation));
        await Reject(db, PostgresErrorCodes.RestrictViolation); db.ChangeTracker.Clear();
        db.Users.Remove(await db.Users.SingleAsync(x => x.Id == seed.User));
        await Reject(db, PostgresErrorCodes.RestrictViolation);
    }
}

public sealed class MessageMigrationTests
{
    [Fact]
    public async Task IdempotencyUpgradeBackfillsDistinctKeysWithoutChangingMessages()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18").Build(); await postgres.StartAsync();
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261103161543_AddMessagePersistence");
        var seed = await MessagePersistenceTests.Seed(db);
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO messages.\"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Body\") VALUES ({first}, {seed.Conversation}, {seed.User}, 'Legacy'), ({second}, {seed.Conversation}, {seed.User}, 'Legacy')");
        await db.Database.MigrateAsync();
        var rows = await db.Messages.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, x => x.Id == first); Assert.Contains(rows, x => x.Id == second);
        Assert.All(rows, x => { Assert.NotEqual(Guid.Empty, x.ClientMessageId); Assert.Equal("Legacy", x.Body); });
        Assert.Equal(2, rows.Select(x => x.ClientMessageId).Distinct().Count());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task UpgradeAndDowngrade_PreserveStage2Data()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18").Build(); await postgres.StartAsync();
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>(); const string stage2 = "20261031110209_AddConversationPersistence";
        await migrator.MigrateAsync(stage2); var seed = await MessagePersistenceTests.Seed(db);
        await db.Database.MigrateAsync();
        db.Messages.Add(new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "Upgrade" }); await db.SaveChangesAsync();
        await migrator.MigrateAsync(stage2); db.ChangeTracker.Clear();
        Assert.Equal(seed.Conversation, (await db.Conversations.SingleAsync()).Id);
        Assert.Equal(seed.User, (await db.ConversationMembers.SingleAsync()).UserId);
        Assert.Equal("Sender", (await db.Profiles.SingleAsync()).DisplayName);
        await db.Database.MigrateAsync(); Assert.Empty(await db.Messages.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
