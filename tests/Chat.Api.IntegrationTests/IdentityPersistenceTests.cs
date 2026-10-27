using System.Net;
using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Chat.Api.IntegrationTests;

public sealed class IdentityPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ChatDatabase"] = _postgres.GetConnectionString()
                })));
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Migration_Identity_Profile_AndHealth_WorkTogether()
    {
        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var user = new ApplicationUser { UserName = "alice", Email = "alice@example.test" };
            var result = await users.CreateAsync(user, "Test-password123!");
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
            userId = user.Id;
            db.Profiles.Add(new UserProfile { UserId = userId, DisplayName = "Alice" });
            await db.SaveChangesAsync();
            var duplicate = await users.CreateAsync(new ApplicationUser
                { UserName = "other", Email = "ALICE@example.test" });
            Assert.Contains(duplicate.Errors, x => x.Code == "DuplicateEmail");
            duplicate = await users.CreateAsync(new ApplicationUser
                { UserName = "ALICE", Email = "other@example.test" });
            Assert.Contains(duplicate.Errors, x => x.Code == "DuplicateUserName");
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.Equal("Alice", (await db.Profiles.SingleAsync(x => x.UserId == userId)).DisplayName);
            Assert.NotNull((await db.Users.SingleAsync(x => x.Id == userId)).PasswordHash);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            await db.Database.MigrateAsync();
        }
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Database_RejectsDuplicateNormalizedCredentials(bool duplicateEmail)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = "one", NormalizedUserName = "ONE",
            Email = "one@example.test", NormalizedEmail = "ONE@EXAMPLE.TEST" });
        await db.SaveChangesAsync();
        db.Users.Add(new ApplicationUser { Id = Guid.NewGuid(), UserName = "two", Email = "two@example.test",
            NormalizedUserName = duplicateEmail ? "TWO" : "ONE",
            NormalizedEmail = duplicateEmail ? "ONE@EXAMPLE.TEST" : "TWO@EXAMPLE.TEST" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task Profile_RequiresUser_IsUnique_AndCascadesOnDelete()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var id = Guid.NewGuid();
        db.Profiles.Add(new UserProfile { UserId = id, DisplayName = "Orphan" });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        var user = new ApplicationUser { Id = id, UserName = "one", NormalizedUserName = "ONE",
            Email = "one@example.test", NormalizedEmail = "ONE@EXAMPLE.TEST" };
        db.Users.Add(user);
        db.Profiles.Add(new UserProfile { UserId = id, DisplayName = "One" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Profiles.Add(new UserProfile { UserId = id, DisplayName = "Duplicate" });
        error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        await db.Users.Where(x => x.Id == id).ExecuteDeleteAsync();
        Assert.False(await db.Profiles.AnyAsync(x => x.UserId == id));
    }
}
