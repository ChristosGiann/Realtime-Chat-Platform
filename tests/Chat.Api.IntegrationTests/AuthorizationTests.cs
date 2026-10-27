using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

// Added to MVC only by this test factory, never to the production application.
[ApiController]
[Route("test/authorization")]
public sealed class AuthorizationProbeController(CurrentUserProbe probe) : ControllerBase
{
    [HttpGet("fallback")]
    public IActionResult Fallback() => Ok(new { id = probe.UserId });

    [Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
    [HttpGet("named")]
    public IActionResult Named() => Ok(new { id = probe.UserId });
}

public sealed class CurrentUserProbe(ICurrentUser currentUser)
{
    public Guid? UserId => currentUser.UserId;
}

public sealed class AuthorizationTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private WebApplicationFactory<Program> Factory() => fixture.Factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "https://frontend.example.test" }));
        builder.ConfigureServices(services =>
        {
            services.AddControllers().AddApplicationPart(typeof(AuthorizationProbeController).Assembly);
            services.AddScoped<CurrentUserProbe>();
        });
    });

    private static HttpClient Client(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });

    private static async Task<Guid> Login(HttpClient client)
    {
        var name = Guid.NewGuid().ToString("N");
        var email = $"{name}@example.test";
        const string password = "Test-password123!";
        var registration = await client.PostAsJsonAsync("/api/auth/register", new { email, userName = name, password });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var user = await registration.Content.ReadFromJsonAsync<JsonElement>();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
        return user.GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("/test/authorization/fallback")]
    [InlineData("/test/authorization/named")]
    [InlineData("/api/auth/me")]
    public async Task AnonymousAccess_IsRejected(string path)
    {
        using var factory = Factory();
        using var client = Client(factory);
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ApplicationService_ResolvesRequestUser_WithoutLeakingBetweenSessions()
    {
        using var factory = Factory();
        using var first = Client(factory);
        using var second = Client(factory);
        using var anonymous = Client(factory);
        var firstId = await Login(first);
        var secondId = await Login(second);
        Assert.NotEqual(firstId, secondId);
        foreach (var path in new[] { "/test/authorization/fallback", "/test/authorization/named", "/api/auth/me" })
        {
            var responses = await Task.WhenAll(first.GetAsync(path), second.GetAsync(path));
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(firstId, (await responses[0].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
            Assert.Equal(secondId, (await responses[1].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/test/authorization/fallback")).StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/api/version")]
    [InlineData("/api/auth/csrf")]
    public async Task ExplicitPublicEndpoints_RemainAnonymous(string path)
    {
        using var factory = Factory();
        using var client = Client(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("https://frontend.example.test", true)]
    [InlineData("https://untrusted.example.test", false)]
    [InlineData("https://frontend.example.test.attacker.test", false)]
    [InlineData("null", false)]
    public async Task Preflight_AllowsOnlyConfiguredOrigin(string origin, bool allowed)
    {
        using var factory = Factory();
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/auth/logout");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
            Assert.Contains("POST", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods")));
        }
        else
        {
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
    }

    [Fact]
    public async Task ConfiguredFrontend_CanReadAuthenticatedUser()
    {
        using var factory = Factory();
        using var client = Client(factory);
        var id = await Login(client);
        client.DefaultRequestHeaders.Add("Origin", "https://frontend.example.test");
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://frontend.example.test", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        Assert.Equal(id, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }
}
