using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Chat.Api.IntegrationTests;

public sealed class SessionFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Production").ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ChatDatabase"] = _postgres.GetConnectionString()
                })));
        using var scope = Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed class SessionTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private const string Password = "Test-password123!";
    private HttpClient Client(WebApplicationFactory<Program>? factory = null) =>
        (factory ?? fixture.Factory).CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

    private static async Task<string> Register(HttpClient client)
    {
        var id = Guid.NewGuid().ToString("N");
        var email = $"{id}@example.test";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, userName = id, password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return email;
    }

    private static async Task Csrf(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("requestToken").GetString());
    }

    [Fact]
    public async Task Login_CurrentUser_Logout_UsesSecureSession()
    {
        using var client = Client();
        var email = await Register(client);
        await Csrf(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = email.ToUpperInvariant(), password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-Chat.Session="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, json.GetProperty("email").GetString());
        Assert.True(Guid.TryParse(json.GetProperty("id").GetString(), out _));
        Assert.Equal(6, json.EnumerateObject().Count());
        Assert.False(json.TryGetProperty("passwordHash", out _));
        await Csrf(client); // Tokens are bound to the current authenticated identity.
        response = await client.PostAsJsonAsync("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("__Host-Chat.Session=;") && value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        await AssertProblem(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnonymousAndTamperedCookies_Return401WithoutRedirect()
    {
        using var client = Client();
        await AssertProblem(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add("Cookie", "__Host-Chat.Session=tampered");
        await AssertProblem(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        await AssertProblem(await client.PostAsJsonAsync("/api/auth/logout", new { }), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownWrongPasswordAndLockedAccount_ReturnSameError()
    {
        using var client = Client();
        var email = await Register(client);
        await Csrf(client);
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { email = "missing@example.test", password = Password });
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Wrong-password123!" });
        var first = await AssertProblem(unknown, HttpStatusCode.Unauthorized);
        var second = await AssertProblem(wrong, HttpStatusCode.Unauthorized);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(email))!;
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
        }
        var locked = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        var third = await AssertProblem(locked, HttpStatusCode.Unauthorized);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.False(wrong.Headers.Contains("Set-Cookie"));
        await AssertProblem(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LoginAndLogout_RejectMissingOrInvalidCsrf()
    {
        using var client = Client();
        var email = await Register(client);
        await AssertProblem(await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password }), HttpStatusCode.BadRequest);
        await Csrf(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        await AssertProblem(await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password }), HttpStatusCode.BadRequest);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password })).StatusCode);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await AssertProblem(await client.PostAsJsonAsync("/api/auth/logout", new { }), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("""{"email":"bad","password":"x"}""")]
    public async Task InvalidLoginInput_ReturnsValidationProblem(string json)
    {
        using var client = Client();
        await Csrf(client);
        var response = await client.PostAsync("/api/auth/login",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        await AssertProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DevelopmentHttp_AllowsLocalCookiesAndCredentialedCors()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        var email = await Register(client);
        await Csrf(client);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("Chat.Session="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Production_DoesNotAllowUnconfiguredCrossOriginAccess()
    {
        using var client = Client();
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        var response = await client.GetAsync("/api/auth/csrf");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("__Host-Chat.Csrf=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredSession_Returns401()
    {
        var clock = new SessionClock();
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                IdentityConstants.ApplicationScheme, options => options.TimeProvider = clock)));
        using var client = Client(factory);
        var email = await Register(client);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password })).StatusCode);
        clock.Advance(TimeSpan.FromHours(9));
        await AssertProblem(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
    }

    private sealed class SessionClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan time) => _now += time;
    }

    private static async Task<string?> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        return json.GetProperty("title").GetString();
    }
}
