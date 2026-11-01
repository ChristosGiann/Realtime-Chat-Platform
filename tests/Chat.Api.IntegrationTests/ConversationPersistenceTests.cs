using Chat.Api.Conversations;
using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Chat.Api.IntegrationTests;

public sealed class ConversationPersistenceTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private IServiceScope Scope() => fixture.Factory.Services.CreateScope();
    private static ChatDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    private static Conversation Group(string name = "Test group") => new() { Type = ConversationType.Group, Name = name };
    private static Conversation Direct(Guid[] users) => new()
        { Type = ConversationType.Direct, DirectUserLowId = users[0], DirectUserHighId = users[1] };
    private static async Task<Guid[]> Users(ChatDbContext db)
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        foreach (var id in ids)
            db.Users.Add(new ApplicationUser { Id = id, UserName = id.ToString("N"), NormalizedUserName = id.ToString("N").ToUpperInvariant(),
                Email = $"{id:N}@example.test", NormalizedEmail = $"{id:N}@example.test".ToUpperInvariant() });
        await db.SaveChangesAsync();
        return ids;
    }
    private static async Task<PostgresException> Reject(ChatDbContext db, string code)
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(code, postgres.SqlState);
        return postgres;
    }

    [Fact]
    public async Task FreshMigration_PersistsBothTypesAndMemberships_WithUtcDefaults()
    {
        Guid groupId;
        Guid directId;
        Guid[] users;
        using (var scope = Scope())
        {
            var db = Db(scope);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            users = await Users(db);
            var group = Group(new string('x', 100));
            var direct = Direct(users);
            groupId = group.Id; directId = direct.Id;
            group.Members.Add(new ConversationMember { UserId = users[0], Role = ConversationMemberRole.Owner });
            group.Members.Add(new ConversationMember { UserId = users[1] });
            foreach (var id in users) direct.Members.Add(new ConversationMember { UserId = id });
            db.Conversations.AddRange(group, direct);
            await db.SaveChangesAsync();
            Assert.NotEqual(Guid.Empty, groupId);
            Assert.NotEqual(default, group.CreatedAt);
        }
        using (var scope = Scope())
        {
            var db = Db(scope);
            await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Pacific/Honolulu'");
            var group = await db.Conversations.Include(x => x.Members).SingleAsync(x => x.Id == groupId);
            Assert.Equal(ConversationType.Group, group.Type);
            Assert.Equal(100, group.Name!.Length);
            Assert.Equal(DateTimeKind.Utc, group.CreatedAt.Kind);
            Assert.Equal(2, group.Members.Count);
            Assert.All(group.Members, member => { Assert.Equal(DateTimeKind.Utc, member.JoinedAt.Kind); Assert.NotEqual(default, member.JoinedAt); });
            Assert.Equal(ConversationMemberRole.Owner, group.Members.Single(x => x.UserId == users[0]).Role);
            var direct = await db.Conversations.Include(x => x.Members).SingleAsync(x => x.Id == directId);
            Assert.Equal(ConversationType.Direct, direct.Type);
            Assert.Null(direct.Name);
            Assert.Equal(users, direct.Members.Select(x => x.UserId).Order());
            Assert.All(direct.Members, member => Assert.Equal(ConversationMemberRole.Member, member.Role));
            await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'UTC'");
        }
    }

    [Theory]
    [InlineData("unknownType")]
    [InlineData("zeroType")]
    [InlineData("emptyId")]
    [InlineData("groupNullName")]
    [InlineData("groupEmptyName")]
    [InlineData("groupWhitespaceName")]
    [InlineData("groupPair")]
    [InlineData("directName")]
    [InlineData("directMissingLow")]
    [InlineData("directMissingHigh")]
    [InlineData("directSelf")]
    [InlineData("directReversed")]
    public async Task DatabaseRejectsInvalidConversationShape(string scenario)
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var conversation = scenario.StartsWith("direct") ? Direct(users) : Group();
        switch (scenario)
        {
            case "unknownType": conversation.Type = (ConversationType)99; break;
            case "zeroType": conversation.Type = 0; break;
            case "emptyId": conversation.Id = Guid.Empty; break;
            case "groupNullName": conversation.Name = null; break;
            case "groupEmptyName": conversation.Name = ""; break;
            case "groupWhitespaceName": conversation.Name = " \t\r\n "; break;
            case "groupPair": conversation.DirectUserLowId = users[0]; conversation.DirectUserHighId = users[1]; break;
            case "directName": conversation.Name = "Not allowed"; break;
            case "directMissingLow": conversation.DirectUserLowId = null; break;
            case "directMissingHigh": conversation.DirectUserHighId = null; break;
            case "directSelf": conversation.DirectUserHighId = users[0]; break;
            case "directReversed": conversation.DirectUserLowId = users[1]; conversation.DirectUserHighId = users[0]; break;
        }
        db.Conversations.Add(conversation);
        await Reject(db, PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task DatabaseRejectsOverlongGroupName()
    {
        using var scope = Scope();
        var db = Db(scope);
        db.Conversations.Add(Group(new string('x', 101)));
        await Reject(db, PostgresErrorCodes.StringDataRightTruncation);
    }

    [Fact]
    public async Task ConcurrentDirectPairs_CannotPersistDuplicates()
    {
        Guid[] users;
        using (var scope = Scope()) users = await Users(Db(scope));
        async Task<bool> Attempt()
        {
            using var scope = Scope();
            var db = Db(scope);
            db.Conversations.Add(Direct(users));
            try { await db.SaveChangesAsync(); return true; }
            catch (DbUpdateException error)
            {
                var postgres = Assert.IsType<PostgresException>(error.InnerException);
                Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
                Assert.Equal("UX_Conversations_DirectPair", postgres.ConstraintName);
                return false;
            }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        Assert.Single(results, result => result);
        using var check = Scope();
        Assert.Equal(1, await Db(check).Conversations.CountAsync(x => x.DirectUserLowId == users[0] && x.DirectUserHighId == users[1]));
    }

    [Fact]
    public async Task MembershipIdentity_IsUniqueWithinConversation_ButAllowsOtherConversations()
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var first = Group(); var second = Group();
        first.Members.Add(new ConversationMember { UserId = users[0] });
        second.Members.Add(new ConversationMember { UserId = users[0] });
        db.Conversations.AddRange(first, second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.ConversationMembers.Add(new ConversationMember { ConversationId = first.Id, UserId = users[0] });
        var error = await Reject(db, PostgresErrorCodes.UniqueViolation);
        Assert.Equal("PK_ConversationMembers", error.ConstraintName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MembershipRequiresExistingConversationAndIdentityUser(bool missingUser)
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var group = Group();
        db.Conversations.Add(group); await db.SaveChangesAsync();
        db.ConversationMembers.Add(new ConversationMember
        { ConversationId = missingUser ? group.Id : Guid.NewGuid(), UserId = missingUser ? Guid.NewGuid() : users[0] });
        await Reject(db, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectPairRequiresBothIdentityUsers(bool missingLow)
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        db.Users.Remove(await db.Users.SingleAsync(x => x.Id == users[missingLow ? 0 : 1]));
        await db.SaveChangesAsync();
        db.Conversations.Add(Direct(users));
        await Reject(db, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task DatabaseRejectsUnknownMembershipRole(int role)
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var group = Group();
        group.Members.Add(new ConversationMember { UserId = users[0], Role = (ConversationMemberRole)role });
        db.Conversations.Add(group);
        await Reject(db, PostgresErrorCodes.CheckViolation);
        db.ChangeTracker.Clear();
        Assert.False(await db.Conversations.AnyAsync(x => x.Id == group.Id));
    }

    [Fact]
    public async Task AtMostOneOwner_ButMultipleOrdinaryMembers()
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var group = Group();
        group.Members.Add(new ConversationMember { UserId = users[0], Role = ConversationMemberRole.Owner });
        group.Members.Add(new ConversationMember { UserId = users[1] });
        db.Conversations.Add(group); await db.SaveChangesAsync();
        group.Members.Single(x => x.UserId == users[1]).Role = ConversationMemberRole.Owner;
        var error = await Reject(db, PostgresErrorCodes.UniqueViolation);
        Assert.Equal("UX_ConversationMembers_Owner", error.ConstraintName);
    }

    [Fact]
    public async Task ConversationDeletion_CascadesMemberships_NotIdentityOrProfiles()
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        db.Profiles.Add(new UserProfile { UserId = users[0], DisplayName = "Keep profile" });
        var group = Group(); group.Members.Add(new ConversationMember { UserId = users[0] });
        var direct = Direct(users); direct.Members.Add(new ConversationMember { UserId = users[0] });
        db.Conversations.AddRange(group, direct); await db.SaveChangesAsync();
        await db.Conversations.Where(x => x.Id == group.Id || x.Id == direct.Id).ExecuteDeleteAsync();
        Assert.False(await db.ConversationMembers.AnyAsync(x => x.ConversationId == group.Id || x.ConversationId == direct.Id));
        Assert.Equal(2, await db.Users.CountAsync(x => users.Contains(x.Id)));
        Assert.True(await db.Profiles.AnyAsync(x => x.UserId == users[0]));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("directLow")]
    [InlineData("directHigh")]
    public async Task UserDeletion_IsRestrictedWhenReferenced(string reference)
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var conversation = reference == "membership" ? Group() : Direct(users);
        if (reference == "membership") conversation.Members.Add(new ConversationMember { UserId = users[0] });
        db.Conversations.Add(conversation); await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Users.Remove(await db.Users.SingleAsync(x => x.Id == users[reference == "directHigh" ? 1 : 0]));
        await Reject(db, PostgresErrorCodes.RestrictViolation);
    }

    [Fact]
    public async Task ExplicitUtcInstants_RoundTrip_AndNonUtcWritesAreRejected()
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var instant = new DateTime(2026, 10, 31, 10, 15, 0, DateTimeKind.Utc);
        var group = Group(); group.CreatedAt = instant;
        group.Members.Add(new ConversationMember { UserId = users[0], JoinedAt = instant });
        db.Conversations.Add(group); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var saved = await db.Conversations.Include(x => x.Members).SingleAsync(x => x.Id == group.Id);
        Assert.Equal(instant, saved.CreatedAt); Assert.Equal(instant, saved.Members.Single().JoinedAt);
        var invalid = Group(); invalid.CreatedAt = DateTime.SpecifyKind(instant, DateTimeKind.Unspecified);
        db.Conversations.Add(invalid);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UTC", error.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DatabaseRejectsInfiniteTimestamps(bool conversationTimestamp)
    {
        using var scope = Scope();
        var db = Db(scope);
        var users = await Users(db);
        var group = Group();
        if (conversationTimestamp) group.CreatedAt = DateTime.MaxValue;
        else group.Members.Add(new ConversationMember { UserId = users[0], JoinedAt = DateTime.MaxValue });
        db.Conversations.Add(group);
        await Reject(db, PostgresErrorCodes.CheckViolation);
    }
}

public sealed class ConversationMigrationTests
{
    [Fact]
    public async Task UpgradeAndDowngrade_PreserveStage1Data_AndLeaveNoPendingMigrations()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18").Build();
        await postgres.StartAsync();
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        const string stage1 = "20261024121917_InitialIdentityProfiles";
        await migrator.MigrateAsync(stage1);
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, UserName = "upgrade", NormalizedUserName = "UPGRADE",
            Email = "upgrade@example.test", NormalizedEmail = "UPGRADE@EXAMPLE.TEST" });
        db.Profiles.Add(new UserProfile { UserId = id, DisplayName = "Preserved" });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var group = new Conversation { Type = ConversationType.Group, Name = "Upgraded" };
        group.Members.Add(new ConversationMember { UserId = id, Role = ConversationMemberRole.Owner });
        db.Conversations.Add(group); await db.SaveChangesAsync();
        await migrator.MigrateAsync(stage1);
        db.ChangeTracker.Clear();
        Assert.Equal("Preserved", (await db.Profiles.SingleAsync()).DisplayName);
        Assert.Equal(id, (await db.Users.SingleAsync()).Id);
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Conversations.ToListAsync());
        Assert.Empty(await db.ConversationMembers.ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        await db.Database.MigrateAsync();
    }
}
