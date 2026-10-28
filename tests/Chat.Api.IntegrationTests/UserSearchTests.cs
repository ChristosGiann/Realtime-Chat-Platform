using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Chat.Api.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class UserSearchTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private HttpClient Client() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private static string Marker() => Guid.NewGuid().ToString("N");

    private static async Task<Guid> Login(HttpClient client, string? displayName = null)
    {
        var userName = Marker();
        var email = $"{userName}@example.test";
        const string password = "Test-password123!";
        var registration = await client.PostAsJsonAsync("/api/auth/register", new { userName, email, password, displayName });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var json = await registration.Content.ReadFromJsonAsync<JsonElement>();
        var csrf = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
        // GET search must work without CSRF headers.
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        return json.GetProperty("id").GetGuid();
    }

    private async Task<Guid> Seed(string displayName, string? userName = null, string? email = null,
        string? bio = null, string? avatarUrl = null, bool withProfile = true)
    {
        var id = Guid.NewGuid();
        userName ??= Marker();
        email ??= $"{id:N}@private.test";
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        db.Users.Add(new ApplicationUser { Id = id, UserName = userName, NormalizedUserName = userName.ToUpperInvariant(),
            Email = email, NormalizedEmail = email.ToUpperInvariant(), PasswordHash = "private-hash", SecurityStamp = "private-stamp" });
        if (withProfile)
            db.Profiles.Add(new UserProfile { UserId = id, DisplayName = displayName, Bio = bio, AvatarUrl = avatarUrl });
        await db.SaveChangesAsync();
        return id;
    }

    private static string Url(string? query = null, int? pageSize = null, string? cursor = null) =>
        "/api/users?query=" + Uri.EscapeDataString(query ?? "") +
        (pageSize is null ? "" : $"&pageSize={pageSize}") +
        (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));

    private static async Task<UserSearchResponse> Search(HttpClient client, string query, int? pageSize = null, string? cursor = null)
    {
        var response = await client.GetAsync(Url(query, pageSize, cursor));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<UserSearchResponse>())!;
    }

    [Fact]
    public async Task AnonymousSearch_Returns401WithoutRedirect()
    {
        using var client = Client();
        var response = await client.GetAsync(Url("Alice"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task MatchesUsernameOrDisplayName_CaseInsensitively_AndExcludesSelfAndMissingProfiles()
    {
        using var client = Client();
        var marker = Marker();
        await Login(client, marker);
        var nameMatch = await Seed("Different name", "prefix" + marker + "suffix");
        var displayMatch = await Seed("Before " + marker + " After");
        await Seed("No match");
        await Seed("No profile", marker + "missing", withProfile: false);
        var result = await Search(client, "  " + marker.ToUpperInvariant() + "  ");
        Assert.Equal(new[] { nameMatch, displayMatch }.Order(), result.Items.Select(item => item.UserId));
        Assert.Null(result.NextCursor);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task LikeMetacharacters_AreLiteral(string character)
    {
        using var client = Client();
        await Login(client);
        var marker = Marker();
        var match = await Seed(marker + character + "suffix");
        await Seed(marker + "other");
        var result = await Search(client, marker + character);
        Assert.Equal(match, Assert.Single(result.Items).UserId);
    }

    [Fact]
    public async Task PrivateFields_AreNeitherSearchedNorReturned()
    {
        using var client = Client();
        await Login(client);
        var privateMarker = Marker();
        var publicMarker = Marker();
        var id = await Seed(publicMarker, email: privateMarker + "@private.test",
            bio: "Bio " + privateMarker, avatarUrl: "https://images.test/" + privateMarker);
        Assert.Empty((await Search(client, privateMarker)).Items);
        var response = await client.GetAsync(Url(publicMarker));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "items", "nextCursor" }, json.EnumerateObject().Select(p => p.Name).Order());
        var item = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "avatarUrl", "bio", "displayName", "userId" }, item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(id, item.GetProperty("userId").GetGuid());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyQuery_ReturnsEmptyPage(string? query)
    {
        using var client = Client();
        await Login(client);
        var response = await client.GetAsync(query is null ? "/api/users" : Url(query));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<UserSearchResponse>();
        Assert.Empty(page!.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task NoMatch_ReturnsEmptyPage()
    {
        using var client = Client();
        await Login(client);
        var page = await Search(client, Marker());
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Pagination_IsDeterministic_Bounded_AndHasNoDuplicates()
    {
        using var client = Client();
        await Login(client);
        var marker = Marker();
        var ids = new List<Guid>();
        for (var i = 0; i < 53; i++) ids.Add(await Seed(marker));
        ids.Sort();
        var first = await Search(client, marker);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(ids.Take(20), first.Items.Select(item => item.UserId));
        var replay = await Search(client, marker);
        Assert.Equal(first.Items, replay.Items);
        Assert.NotNull(first.NextCursor);
        var second = await Search(client, marker, cursor: first.NextCursor);
        var third = await Search(client, marker, cursor: second.NextCursor);
        Assert.Equal(20, second.Items.Count);
        Assert.Equal(13, third.Items.Count);
        Assert.Null(third.NextCursor);
        Assert.Equal(ids, first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.UserId));
        Assert.Equal(50, (await Search(client, marker, 50)).Items.Count);
        var small = await Search(client, marker, 1);
        Assert.Single(small.Items);
        Assert.Equal(ids[1], Assert.Single((await Search(client, marker, 1, small.NextCursor)).Items).UserId);
    }

    [Fact]
    public async Task Cursor_ContinuesAfterDeletionAndRename_AndExactPageHasNoNextCursor()
    {
        using var client = Client();
        await Login(client);
        var marker = Marker();
        var ids = new[] { await Seed(marker), await Seed(marker), await Seed(marker) }.Order().ToArray();
        var first = await Search(client, marker, 1);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await db.Users.Where(user => user.Id == ids[0]).ExecuteDeleteAsync();
            await db.Profiles.Where(profile => profile.UserId == ids[1])
                .ExecuteUpdateAsync(setters => setters.SetProperty(profile => profile.DisplayName, "Updated " + marker));
        }
        var next = await Search(client, marker, 2, first.NextCursor);
        Assert.Equal(ids.Skip(1), next.Items.Select(item => item.UserId));
        Assert.Null(next.NextCursor);
    }

    [Fact]
    public async Task InvalidTamperedOrMismatchedCursors_Return400()
    {
        using var client = Client();
        using var other = Client();
        await Login(client);
        await Login(other);
        var marker = Marker();
        await Seed(marker);
        await Seed(marker);
        var cursor = (await Search(client, marker, 1)).NextCursor!;
        var tampered = (cursor[0] == 'A' ? "B" : "A") + cursor[1..];
        foreach (var invalid in new[] { "invalid", tampered })
            await BadRequest(client, Url(marker, 1, invalid));
        await BadRequest(client, Url("different", 1, cursor));
        await BadRequest(client, Url("", 1, cursor));
        await BadRequest(other, Url(marker, 1, cursor));
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=51")]
    [InlineData("pageSize=2147483648")]
    [InlineData("pageSize=abc")]
    public async Task InvalidPageSize_Returns400(string parameters)
    {
        using var client = Client();
        await Login(client);
        await BadRequest(client, "/api/users?query=test&" + parameters);
    }

    [Fact]
    public async Task OversizedInput_Returns400_AndMaximumQueryLengthIsAccepted()
    {
        using var client = Client();
        await Login(client);
        await BadRequest(client, Url(new string('a', 101)));
        await BadRequest(client, Url("test", cursor: new string('a', 4097)));
        var query = new string('x', 100);
        var id = await Seed(query);
        Assert.Equal(id, Assert.Single((await Search(client, query)).Items).UserId);
    }

    private static async Task BadRequest(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
