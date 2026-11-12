using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace Chat.Api.IntegrationTests;

public sealed class DeliveryMigrationTests
{
    [Fact]
    public async Task DeliveryMigrationBackfillsReadsAndRoundTripsWithoutLosingHistory()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18").Build(); await postgres.StartAsync();
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>(); const string previous = "20261104104930_AddMembershipReadState";
        await migrator.MigrateAsync(previous); var seed = await MessagePersistenceTests.Seed(db);
        var message = new Message { ConversationId = seed.Conversation, SenderId = seed.User, Body = "Already read" };
        db.Messages.Add(message); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE conversations.\"ConversationMembers\" SET \"LastReadMessageId\" = {message.Id} WHERE \"ConversationId\" = {seed.Conversation}");
        await db.Database.MigrateAsync();
        var member = await db.ConversationMembers.SingleAsync();
        Assert.Equal(message.Id, member.LastDeliveredMessageId); Assert.Equal(message.Id, member.LastReadMessageId);
        Assert.NotEqual(Guid.Empty, member.MembershipId); var incarnation = member.MembershipId;
        await migrator.MigrateAsync(previous); db.ChangeTracker.Clear();
        Assert.Equal(message.Id, await db.ConversationMembers.Select(x => x.LastReadMessageId).SingleAsync());
        Assert.Equal(message.Id, (await db.Messages.SingleAsync()).Id);
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        member = await db.ConversationMembers.SingleAsync();
        Assert.Equal(message.Id, member.LastDeliveredMessageId); Assert.NotEqual(incarnation, member.MembershipId);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
    }
}
