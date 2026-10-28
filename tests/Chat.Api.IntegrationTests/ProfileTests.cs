using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class ProfileTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private HttpClient Client() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private static async Task<Guid> RegisterAndLogin(HttpClient client)
    {
        var userName = Guid.NewGuid().ToString("N");
        var email = $"{userName}@example.test";
        const string password = "Test-password123!";
        var registration = await client.PostAsJsonAsync("/api/auth/register", new { userName, email, password });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var json = await registration.Content.ReadFromJsonAsync<JsonElement>();
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
        await Csrf(client);
        return json.GetProperty("id").GetGuid();
    }

    private static async Task Csrf(HttpClient client)
    {
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
    }

    [Fact]
    public async Task UpdateOwnProfile_PersistsAndIsPublic_WithoutIdentityData()
    {
        using var client = Client();
        using var anonymous = Client();
        var id = await RegisterAndLogin(client);
        var response = await client.PutAsJsonAsync("/api/profiles/me", new
        {
            displayName = "  Alice  ", bio = "  Hello world  ", avatarUrl = " https://images.example.test/alice.png "
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<PublicProfileResponse>();
        Assert.Equal(new PublicProfileResponse(id, "Alice", "Hello world", "https://images.example.test/alice.png"), updated);
        response = await anonymous.GetAsync($"/api/profiles/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "avatarUrl", "bio", "displayName", "userId" },
            json.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
        Assert.Equal(id, json.GetProperty("userId").GetGuid());
        Assert.Equal("Alice", json.GetProperty("displayName").GetString());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var profile = await db.Profiles.AsNoTracking().SingleAsync(profile => profile.UserId == id);
        Assert.Equal("Hello world", profile.Bio);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("Alice", me.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Put_ReplacesOptionalFields_WithNull()
    {
        using var client = Client();
        var id = await RegisterAndLogin(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/profiles/me",
            new { displayName = "Alice", bio = "Bio", avatarUrl = "https://images.example.test/a.png" })).StatusCode);
        var response = await client.PutAsJsonAsync("/api/profiles/me", new { displayName = "Alice Updated", bio = " " });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PublicProfileResponse>();
        Assert.Equal(id, dto!.UserId);
        Assert.Null(dto.Bio);
        Assert.Null(dto.AvatarUrl);
    }

    [Fact]
    public async Task AnonymousUpdate_Returns401_WithoutRedirect()
    {
        using var client = Client();
        var response = await client.PutAsJsonAsync("/api/profiles/me", new { displayName = "Intruder" });
        await Problem(response, HttpStatusCode.Unauthorized);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_RequiresValidCsrf_AndDoesNotPersist(bool invalid)
    {
        using var client = Client();
        var id = await RegisterAndLogin(client);
        var before = await client.GetFromJsonAsync<PublicProfileResponse>($"/api/profiles/{id}");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (invalid) client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        await Problem(await client.PutAsJsonAsync("/api/profiles/me", new { displayName = "Changed" }), HttpStatusCode.BadRequest);
        Assert.Equal(before, await client.GetFromJsonAsync<PublicProfileResponse>($"/api/profiles/{id}"));
    }

    [Fact]
    public async Task CannotTargetAnotherUser_ThroughBodyRouteOrQuery()
    {
        using var alice = Client();
        using var bob = Client();
        var aliceId = await RegisterAndLogin(alice);
        var bobId = await RegisterAndLogin(bob);
        var before = await bob.GetFromJsonAsync<PublicProfileResponse>($"/api/profiles/{bobId}");
        await Problem(await alice.PutAsJsonAsync("/api/profiles/me", new { userId = bobId, displayName = "Intruder" }),
            HttpStatusCode.BadRequest);
        var route = await alice.PutAsJsonAsync($"/api/profiles/{bobId}", new { displayName = "Intruder" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, route.StatusCode);
        var query = await alice.PutAsJsonAsync($"/api/profiles/me?userId={bobId}", new { displayName = "Alice Updated" });
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal(aliceId, (await query.Content.ReadFromJsonAsync<PublicProfileResponse>())!.UserId);
        Assert.Equal(before, await bob.GetFromJsonAsync<PublicProfileResponse>($"/api/profiles/{bobId}"));
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("bio")]
    [InlineData("avatarUrl")]
    [InlineData("blankName")]
    [InlineData("http")]
    [InlineData("javascript")]
    [InlineData("relative")]
    [InlineData("credentials")]
    public async Task InvalidFields_Return400_AndLeaveStoredProfileUnchanged(string scenario)
    {
        using var client = Client();
        var id = await RegisterAndLogin(client);
        var before = await client.GetFromJsonAsync<PublicProfileResponse>($"/api/profiles/{id}");
        var displayName = scenario switch { "displayName" => new string('a', 101), "blankName" => " ", _ => "Alice" };
        var bio = scenario == "bio" ? new string('b', 1001) : null;
        var avatarUrl = scenario switch
        {
            "avatarUrl" => "https://images.example.test/" + new string('a', 2048),
            "http" => "http://images.example.test/a.png",
            "javascript" => "javascript:alert(1)",
            "relative" => "/avatar.png",
            "credentials" => "https://user:password@images.example.test/a.png",
            _ => null
        };
        var response = await client.PutAsJsonAsync("/api/profiles/me", new { displayName, bio, avatarUrl });
        var json = await Problem(response, HttpStatusCode.BadRequest);
        var field = scenario switch { "displayName" or "blankName" => "DisplayName", "bio" => "Bio", _ => "AvatarUrl" };
        Assert.True(json.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(before, await client.GetFromJsonAsync<PublicProfileResponse>($"/api/profiles/{id}"));
    }

    [Fact]
    public async Task MaximumLengths_AreAccepted()
    {
        using var client = Client();
        await RegisterAndLogin(client);
        const string prefix = "https://images.example.test/";
        var request = new { displayName = new string('a', 100), bio = new string('b', 1000),
            avatarUrl = prefix + new string('c', 2048 - prefix.Length) };
        var response = await client.PutAsJsonAsync("/api/profiles/me", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<PublicProfileResponse>();
        Assert.Equal(request.avatarUrl, dto!.AvatarUrl);
        Assert.Equal(request.bio, dto.Bio);
        Assert.Equal(request.displayName, dto.DisplayName);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"displayName":42}""")]
    public async Task MalformedInput_ReturnsProblem(string json)
    {
        using var client = Client();
        await RegisterAndLogin(client);
        await Problem(await client.PutAsync("/api/profiles/me",
            new StringContent(json, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MissingUserOrProfile_ReturnsConsistent404()
    {
        using var client = Client();
        using var anonymous = Client();
        var id = await RegisterAndLogin(client);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ChatDbContext>().Profiles
                .Where(profile => profile.UserId == id).ExecuteDeleteAsync();
        }
        var unknown = await Problem(await anonymous.GetAsync($"/api/profiles/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
        var missing = await Problem(await anonymous.GetAsync($"/api/profiles/{id}"), HttpStatusCode.NotFound);
        var update = await Problem(await client.PutAsJsonAsync("/api/profiles/me", new { displayName = "Alice" }), HttpStatusCode.NotFound);
        Assert.Equal(unknown.GetProperty("title").GetString(), missing.GetProperty("title").GetString());
        Assert.Equal(unknown.GetProperty("title").GetString(), update.GetProperty("title").GetString());
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        return json;
    }
}
