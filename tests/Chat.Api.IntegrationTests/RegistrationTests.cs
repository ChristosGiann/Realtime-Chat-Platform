using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chat.Api.Registration;
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

public sealed class RegistrationTests : IAsyncLifetime
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

    private static object Request(string email = "alice@example.test", string userName = "alice",
        string password = "Test-password123!", string? displayName = null) =>
        new { email, userName, password, displayName };

    [Fact]
    public async Task Registration_PersistsUserAndDefaultProfile_WithoutExposingSecrets()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", Request());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var dto = JsonSerializer.Deserialize<RegistrationResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("alice", dto.DisplayName);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var user = await db.Users.SingleAsync();
        var profile = await db.Profiles.SingleAsync();
        Assert.Equal(dto.Id, user.Id);
        Assert.Equal(user.Id, profile.UserId);
        Assert.Null(profile.Bio);
        Assert.Null(profile.AvatarUrl);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.True(await users.CheckPasswordAsync(user, "Test-password123!"));
        Assert.NotEqual("Test-password123!", user.PasswordHash);
    }

    [Fact]
    public async Task Registration_TrimsPublicFields_AndUsesExplicitDisplayName()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            Request(userName: " alice ", displayName: " Alice Example "));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<RegistrationResponse>();
        Assert.Equal("alice", dto!.UserName);
        Assert.Equal("Alice Example", dto.DisplayName);
    }

    [Theory]
    [InlineData("bad", "alice", "Test-password123!", null, "Email")]
    [InlineData("alice@example.test", " ", "Test-password123!", null, "UserName")]
    [InlineData("alice@example.test", "invalid name", "Test-password123!", null, "UserName")]
    [InlineData("alice@example.test", "alice", "weak", null, "Password")]
    [InlineData("alice@example.test", "alice", "alllowercase", null, "Password")]
    [InlineData("alice@example.test", "alice", "Test-password123!", " ", "DisplayName")]
    public async Task InvalidRegistration_ReturnsProblem_AndPersistsNothing(
        string email, string userName, string password, string? displayName, string field)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", Request(email, userName, password, displayName));
        await AssertProblem(response, HttpStatusCode.BadRequest, field);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.False(await db.Users.AnyAsync());
        Assert.False(await db.Profiles.AnyAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"email\":42}")]
    public async Task MalformedOrMissingInput_ReturnsProblem(string json)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/auth/register", new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DuplicateCredentials_ReturnConflict_AndLeaveOneAccount(bool email)
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", Request())).StatusCode);
        var response = await client.PostAsJsonAsync("/api/auth/register",
            Request(email ? "ALICE@example.test" : "other@example.test", email ? "other" : "ALICE"));
        await AssertProblem(response, HttpStatusCode.Conflict, email ? "Email" : "UserName");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.Profiles.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentDuplicates_CreateExactlyOneAccount(bool sameEmail)
    {
        using var client = _factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            client.PostAsJsonAsync("/api/auth/register",
                Request(sameEmail ? "alice@example.test" : $"alice{i}@example.test",
                    sameEmail ? $"alice{i}" : "alice"))));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        foreach (var response in responses.Where(response => response.StatusCode != HttpStatusCode.Created))
            await AssertProblem(response, HttpStatusCode.Conflict);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.Profiles.CountAsync());
    }

    [Fact]
    public async Task ProfileWriteFailure_RollsBackIdentityUser()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        // Inject a database failure after Identity has saved the user.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE profiles."UserProfiles" ADD CONSTRAINT reject_test_profile CHECK ("DisplayName" <> 'Reject')
            """);
        using var client = _factory.CreateClient();
        try
        {
            var response = await client.PostAsJsonAsync("/api/auth/register", Request(displayName: "Reject"));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        catch (DbUpdateException)
        {
            // TestServer may propagate an unhandled server exception.
        }
        Assert.False(await db.Users.AnyAsync());
        Assert.False(await db.Profiles.AnyAsync());
    }

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string? field = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal((int)status, document.RootElement.GetProperty("status").GetInt32());
        if (field is not null)
            Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(field, out _));
    }
}
