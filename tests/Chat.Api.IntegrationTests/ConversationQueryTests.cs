using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chat.Api.Conversations;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class ConversationQueryTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private HttpClient Client() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    private static async Task<Guid> Login(HttpClient client)
    {
        var userName = Guid.NewGuid().ToString("N");
        var email = $"{userName}@example.test";
        const string password = "Test-password123!";
        var response = await client.PostAsJsonAsync("/api/auth/register", new { email, password, userName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Csrf(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/auth/login", new { email, password })).StatusCode);
        await Csrf(client);
        return id;
    }
    private static async Task Csrf(HttpClient client)
    {
        var json = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", json.GetProperty("requestToken").GetString());
    }
    private static Task<HttpResponseMessage> Create(HttpClient client, Guid targetUserId) =>
        client.PostAsJsonAsync("/api/conversations/direct", new { targetUserId });
    private ChatDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<Guid> Group(HttpClient client, params Guid[] members)
    {
        var response = await client.PostAsJsonAsync("/api/conversations/group", new { name = "Membership group", memberIds = members });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GroupConversationResponse>())!.Id;
    }
    private static Task<HttpResponseMessage> Add(HttpClient client, Guid group, Guid user) =>
        client.PutAsync($"/api/conversations/{group}/members/{user}", null);
    private static Task<HttpResponseMessage> Remove(HttpClient client, Guid group, Guid user) =>
        client.DeleteAsync($"/api/conversations/{group}/members/{user}");
    private static Task<HttpResponseMessage> Leave(HttpClient client, Guid group) =>
        client.DeleteAsync($"/api/conversations/{group}/membership");

    [Fact]
    public async Task ListsOnlyCurrentMemberships_WithStablePaginationAndSafeDetails()
    {
        using var alice = Client(); using var bob = Client(); using var outsider = Client();
        var a = await Login(alice); var b = await Login(bob); await Login(outsider);
        var direct = (await (await Create(alice, b)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id;
        var group = await Group(alice, b); var hidden = await Group(outsider);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.Conversations.Where(x => x.Id == group || x.Id == direct).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, timestamp));
        var first = (await alice.GetFromJsonAsync<ConversationPage>("/api/conversations?limit=1"))!;
        var second = (await alice.GetFromJsonAsync<ConversationPage>($"/api/conversations?limit=1&offset={first.NextOffset}"))!;
        Assert.Equal(new[] { direct, group }.Order(), first.Items.Concat(second.Items).Select(x => x.Id));
        Assert.Equal(1, first.NextOffset); Assert.Null(second.NextOffset);
        Assert.DoesNotContain(first.Items.Concat(second.Items), x => x.Id == hidden);
        Assert.Equal("Owner", first.Items.Concat(second.Items).Single(x => x.Id == group).MyRole);
        Assert.Equal("Direct", first.Items.Concat(second.Items).Single(x => x.Id == direct).Type);
        var response = await bob.GetAsync($"/api/conversations/{group}?limit=1");
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "createdAt", "id", "memberCount", "members", "myRole", "name", "nextOffset", "type" }, json.EnumerateObject().Select(x => x.Name).Order());
        var details = json.Deserialize<ConversationDetails>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Member", details.MyRole); Assert.Equal(2, details.MemberCount); Assert.Single(details.Members); Assert.Equal(1, details.NextOffset);
        var next = (await bob.GetFromJsonAsync<ConversationDetails>($"/api/conversations/{group}?limit=1&offset=1"))!;
        Assert.Equal(new[] { a, b }.Order(), details.Members.Concat(next.Members).Select(x => x.UserId));
        Assert.Null(next.NextOffset);
        var profile = json.GetProperty("members")[0].GetProperty("profile");
        Assert.Equal(new[] { "avatarUrl", "bio", "displayName", "userId" }, profile.EnumerateObject().Select(x => x.Name).Order());
        await Problem(await outsider.GetAsync($"/api/conversations/{group}"), HttpStatusCode.NotFound);
        await Problem(await alice.GetAsync($"/api/conversations/{Guid.NewGuid()}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MissingProfile_RemainsSafe_AndRemovalImmediatelyRevokesQueries()
    {
        using var owner = Client(); using var member = Client(); var a = await Login(owner); var b = await Login(member);
        var group = await Group(owner, b);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await db.Profiles.Where(x => x.UserId == b).ExecuteDeleteAsync();
        var details = (await member.GetFromJsonAsync<ConversationDetails>($"/api/conversations/{group}"))!;
        Assert.Null(details.Members.Single(x => x.UserId == b).Profile);
        Assert.NotNull(details.Members.Single(x => x.UserId == a).Profile);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, group, b)).StatusCode);
        Assert.Empty((await member.GetFromJsonAsync<ConversationPage>("/api/conversations"))!.Items);
        await Problem(await member.GetAsync($"/api/conversations/{group}"), HttpStatusCode.NotFound);
        await db.Conversations.Where(x => x.Id == group).ExecuteDeleteAsync();
        await Problem(await owner.GetAsync($"/api/conversations/{group}"), HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=51")]
    [InlineData("offset=-1")]
    [InlineData("offset=2147483647")]
    [InlineData("limit=invalid")]
    public async Task PaginationValidation_AppliesToBothQueries(string query)
    {
        using var client = Client(); await Login(client); var group = await Group(client);
        await Problem(await client.GetAsync($"/api/conversations?{query}"), HttpStatusCode.BadRequest);
        await Problem(await client.GetAsync($"/api/conversations/{group}?{query}"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnonymousAndDeletedIdentity_Are401_AndEmptyListIsValid()
    {
        using var client = Client();
        await Problem(await client.GetAsync("/api/conversations"), HttpStatusCode.Unauthorized);
        await Problem(await client.GetAsync($"/api/conversations/{Guid.NewGuid()}"), HttpStatusCode.Unauthorized);
        var id = await Login(client);
        Assert.Empty((await client.GetFromJsonAsync<ConversationPage>("/api/conversations"))!.Items);
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Users.Where(x => x.Id == id).ExecuteDeleteAsync();
        await Problem(await client.GetAsync("/api/conversations"), HttpStatusCode.Unauthorized);
        await Problem(await client.GetAsync($"/api/conversations/{Guid.NewGuid()}"), HttpStatusCode.Unauthorized);
    }
}
