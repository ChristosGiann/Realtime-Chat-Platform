using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace Chat.Api.IntegrationTests;

public sealed class ReadStateMigrationTests
{
    [Fact]
    public async Task UpgradeAndDowngradePreserveMessagesAndMembership()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18").Build(); await postgres.StartAsync();
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>(); const string previous = "20261104082652_AddMessageIdempotency";
        await migrator.MigrateAsync(previous);
        var seed = await MessagePersistenceTests.Seed(db);
        var message = new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "Retained" };
        db.Messages.Add(message); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        var member = await db.ConversationMembers.SingleAsync(); Assert.Null(member.LastReadMessageId);
        member.LastReadMessageId = message.Id; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.Equal(message.Id, (await db.ConversationMembers.SingleAsync()).LastReadMessageId);
        await migrator.MigrateAsync(previous); db.ChangeTracker.Clear();
        Assert.Equal(message.Id, (await db.Messages.SingleAsync()).Id);
        Assert.Equal(seed.User, await db.ConversationMembers.Select(x => x.UserId).SingleAsync());
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        Assert.Null((await db.ConversationMembers.SingleAsync()).LastReadMessageId);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
