using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Chat.Api.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class Stage1FlowTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    [Fact]
    public async Task CommittedMigrationsAndCompleteIdentityFlow_WorkOnCleanPostgres()
    {
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
        });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var marker = Guid.NewGuid().ToString("N");
        var email = $"{marker}@example.test";
        const string password = "Test-password123!";
        var registration = await client.PostAsJsonAsync("/api/auth/register", new { email, password, userName = marker });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var id = (await registration.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(id, me.GetProperty("id").GetGuid());
        await Csrf(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/profiles/me", new { displayName = "Updated", bio = "Public bio" })).StatusCode);
        var own = await client.GetFromJsonAsync<PublicProfileResponse>($"/api/profiles/{id}");
        Assert.Equal("Updated", own!.DisplayName);
        var peer = await client.PostAsJsonAsync("/api/auth/register", new { email = $"peer{email}", password, userName = $"peer{marker}", displayName = $"Peer {marker}" });
        Assert.Equal(HttpStatusCode.Created, peer.StatusCode);
        var peerId = (await peer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var result = await client.GetFromJsonAsync<UserSearchResponse>($"/api/users?query={marker}");
        Assert.Equal(peerId, Assert.Single(result!.Items).UserId);
        Assert.Null(result.NextCursor);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/users?query={marker}")).StatusCode);
        var publicJson = await client.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}");
        Assert.Equal(new[] { "avatarUrl", "bio", "displayName", "userId" }, publicJson.EnumerateObject().Select(p => p.Name).Order());
    }

    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("requestToken").GetString());
    }
}
