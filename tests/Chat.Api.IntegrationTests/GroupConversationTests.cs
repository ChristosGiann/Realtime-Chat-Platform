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

public sealed class GroupConversationTests(SessionFixture fixture) : IClassFixture<SessionFixture>
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
    private static Task<HttpResponseMessage> Create(HttpClient client, string name, params Guid[] memberIds) =>
        client.PostAsJsonAsync("/api/conversations/group", new { name, memberIds });
    private ChatDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesGroup_WithOneOwnerAndRequestedMembers(bool includeCreator)
    {
        using var alice = Client(); using var bob = Client(); using var carol = Client();
        var a = await Login(alice); var b = await Login(bob); var c = await Login(carol);
        var response = await Create(alice, "  Ομάδα ανάπτυξης  ", includeCreator ? [a, b, c] : [b, c]);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "createdAt", "id", "members", "name", "type" }, json.EnumerateObject().Select(x => x.Name).Order());
        foreach (var member in json.GetProperty("members").EnumerateArray())
            Assert.Equal(new[] { "joinedAt", "role", "userId" }, member.EnumerateObject().Select(x => x.Name).Order());
        var dto = json.Deserialize<GroupConversationResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Group", dto.Type);
        Assert.Equal("Ομάδα ανάπτυξης", dto.Name);
        Assert.Equal(DateTimeKind.Utc, dto.CreatedAt.Kind);
        Assert.Equal(new[] { a, b, c }.Order(), dto.Members.Select(x => x.UserId));
        Assert.Equal(a, Assert.Single(dto.Members, x => x.Role == "Owner").UserId);
        Assert.All(dto.Members.Where(x => x.UserId != a), x => Assert.Equal("Member", x.Role));
        Assert.All(dto.Members, x => Assert.Equal(DateTimeKind.Utc, x.JoinedAt.Kind));
        using var scope = fixture.Factory.Services.CreateScope();
        var saved = await Db(scope).Conversations.Include(x => x.Members).SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(dto.Name, saved.Name);
        Assert.Equal(dto.CreatedAt, saved.CreatedAt);
        Assert.Null(saved.DirectUserLowId); Assert.Null(saved.DirectUserHighId);
        Assert.Equal(3, saved.Members.Count);
        Assert.Equal(a, Assert.Single(saved.Members, x => x.Role == ConversationMemberRole.Owner).UserId);
        Assert.All(saved.Members.Where(x => x.UserId != a), x => Assert.Equal(ConversationMemberRole.Member, x.Role));
    }

    [Fact]
    public async Task EmptyMemberList_CreatesOwnerOnly_AndRepeatedNamesCreateSeparateGroups()
    {
        using var client = Client(); var id = await Login(client);
        var ids = new List<Guid>();
        foreach (var response in new[] { await Create(client, new string('x', 100)), await Create(client, new string('x', 100)) })
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var dto = (await response.Content.ReadFromJsonAsync<GroupConversationResponse>())!;
            Assert.Equal(id, Assert.Single(dto.Members).UserId);
            Assert.Equal("Owner", dto.Members[0].Role);
            ids.Add(dto.Id);
        }
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public async Task UnknownMember_RejectsWholeRequest()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        await Problem(await Create(alice, "Unknown member", b, Guid.NewGuid()), HttpStatusCode.NotFound);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).ConversationMembers.AnyAsync(x => x.UserId == a || x.UserId == b));
        Assert.False(await Db(scope).Conversations.AnyAsync(x => x.Name == "Unknown member"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateIds_AreRejectedWithoutWrites(bool duplicateCreator)
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        var duplicate = duplicateCreator ? a : b;
        await Problem(await Create(alice, "Duplicates", duplicate, duplicate), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).ConversationMembers.AnyAsync(x => x.UserId == a || x.UserId == b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("\u00a0")]
    public async Task BlankName_IsRejected(string? name)
    {
        using var client = Client(); var id = await Login(client);
        await Problem(await client.PostAsJsonAsync("/api/conversations/group", new { name, memberIds = Array.Empty<Guid>() }), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).ConversationMembers.AnyAsync(x => x.UserId == id));
    }

    [Fact]
    public async Task LongName_IsRejected()
    {
        using var client = Client(); await Login(client);
        await Problem(await Create(client, new string('x', 101)), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"name":"Group"}""")]
    [InlineData("""{"name":"Group","memberIds":null}""")]
    [InlineData("""{"name":"Group","memberIds":["invalid"]}""")]
    [InlineData("""{"name":"Group","memberIds":[null]}""")]
    [InlineData("""{"name":"Group","memberIds":["00000000-0000-0000-0000-000000000000"]}""")]
    [InlineData("""{"name":"Group","memberIds":[],"role":"Owner"}""")]
    public async Task InvalidBody_Is400(string body)
    {
        using var client = Client(); await Login(client);
        await Problem(await client.PostAsync("/api/conversations/group", new StringContent(body, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Anonymous_Is401WithoutRedirect()
    {
        using var client = Client();
        var response = await Create(client, "Anonymous");
        await Problem(response, HttpStatusCode.Unauthorized);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CsrfIsRequired(bool invalid)
    {
        using var client = Client(); var id = await Login(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (invalid) client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        await Problem(await Create(client, "CSRF"), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).ConversationMembers.AnyAsync(x => x.UserId == id));
    }

    [Fact]
    public async Task MissingProfile_DoesNotPreventMembership()
    {
        using var alice = Client(); using var bob = Client();
        await Login(alice); var b = await Login(bob);
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Profiles.Where(x => x.UserId == b).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Created, (await Create(alice, "Identity members", b)).StatusCode);
    }

    [Fact]
    public async Task DeletedCallerSession_Is401()
    {
        using var client = Client(); var id = await Login(client);
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Users.Where(x => x.Id == id).ExecuteDeleteAsync();
        await Problem(await Create(client, "Deleted caller"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FailedMembershipInsert_RollsBackGroupAndAllMembers()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" ADD CONSTRAINT \"CK_Test_RejectGroupMember\" CHECK (\"Role\" <> 1) NOT VALID");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Create(alice, "Rollback group", b));
            Assert.False(await db.Conversations.AnyAsync(x => x.Name == "Rollback group"));
            Assert.False(await db.ConversationMembers.AnyAsync(x => x.UserId == a || x.UserId == b));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" DROP CONSTRAINT \"CK_Test_RejectGroupMember\"");
        }
    }
}
