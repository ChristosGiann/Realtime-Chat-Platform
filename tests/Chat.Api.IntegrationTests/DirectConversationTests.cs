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

public sealed class DirectConversationTests(SessionFixture fixture) : IClassFixture<SessionFixture>
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

    [Fact]
    public async Task CreatesExactlyTwoMembers_WithSafeDto_AndReusesInBothDirections()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        var response = await Create(alice, b);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "createdAt", "id", "memberIds", "type" }, json.EnumerateObject().Select(x => x.Name).Order());
        var dto = json.Deserialize<DirectConversationResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Direct", dto.Type);
        Assert.Equal(DateTimeKind.Utc, dto.CreatedAt.Kind);
        Assert.Equal(new[] { a, b }.Order(), dto.MemberIds);
        foreach (var repeated in new[] { await Create(alice, b), await Create(bob, a) })
        {
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            Assert.Equal(dto.Id, (await repeated.Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id);
        }
        using var scope = fixture.Factory.Services.CreateScope();
        var saved = await Db(scope).Conversations.Include(x => x.Members).SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(2, saved.Members.Count);
        Assert.All(saved.Members, x => { Assert.Equal(ConversationMemberRole.Member, x.Role); Assert.Equal(DateTimeKind.Utc, x.JoinedAt.Kind); });
        Assert.Equal(dto.CreatedAt, saved.CreatedAt);
        Assert.Null(saved.Name);
    }

    [Fact]
    public async Task ConcurrentOppositeRequests_ReturnOneConversationAndExactlyTwoMemberships()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => i % 2 == 0 ? Create(alice, b) : Create(bob, a)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(19, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        var ids = new List<Guid>();
        foreach (var response in responses) ids.Add((await response.Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id);
        var id = Assert.Single(ids.Distinct());
        using var scope = fixture.Factory.Services.CreateScope();
        var db = Db(scope);
        Assert.Equal(2, await db.ConversationMembers.CountAsync(x => x.ConversationId == id));
        var pair = new[] { a, b }.Order().ToArray();
        Assert.Equal(1, await db.Conversations.CountAsync(x => x.DirectUserLowId == pair[0] && x.DirectUserHighId == pair[1]));
    }

    [Fact]
    public async Task AnonymousRequest_Is401WithoutRedirect()
    {
        using var client = Client();
        var response = await Create(client, Guid.NewGuid());
        await Problem(response, HttpStatusCode.Unauthorized);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CsrfIsRequired_AndRejectedRequestCreatesNothing(bool invalid)
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        alice.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        if (invalid) alice.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "invalid");
        await Problem(await Create(alice, b), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).ConversationMembers.AnyAsync(x => x.UserId == a || x.UserId == b));
    }

    [Fact]
    public async Task SelfAndUnknownTarget_AreRejectedWithoutPersistence()
    {
        using var client = Client();
        var id = await Login(client);
        await Problem(await Create(client, id), HttpStatusCode.BadRequest);
        await Problem(await Create(client, Guid.NewGuid()), HttpStatusCode.NotFound);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).Conversations.AnyAsync(x => x.DirectUserLowId == id || x.DirectUserHighId == id));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"targetUserId":null}""")]
    [InlineData("""{"targetUserId":"invalid"}""")]
    [InlineData("""{"targetUserId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"targetUserId":42}""")]
    public async Task InvalidBody_Is400(string body)
    {
        using var client = Client(); await Login(client);
        await Problem(await client.PostAsync("/api/conversations/direct", new StringContent(body, Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CallerCannotSupplyInitiatorRoleOrConversationId()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        await Problem(await alice.PostAsJsonAsync("/api/conversations/direct", new { targetUserId = b, userId = b, role = "Owner", conversationId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).ConversationMembers.AnyAsync(x => x.UserId == a || x.UserId == b));
    }

    [Fact]
    public async Task MissingProfileDoesNotPreventIdentityMembership_AndGroupsAreNotReused()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = Db(scope);
        await db.Profiles.Where(x => x.UserId == b).ExecuteDeleteAsync();
        var group = new Conversation { Type = ConversationType.Group, Name = "Existing group" };
        group.Members.Add(new ConversationMember { UserId = a, Role = ConversationMemberRole.Owner });
        group.Members.Add(new ConversationMember { UserId = b });
        db.Conversations.Add(group); await db.SaveChangesAsync();
        var response = await Create(alice, b);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(group.Id, (await response.Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id);
        Assert.Equal(2, await db.ConversationMembers.CountAsync(x => x.ConversationId == group.Id));
    }

    [Fact]
    public async Task DeletedCallerSession_Is401()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Users.Where(x => x.Id == a).ExecuteDeleteAsync();
        await Problem(await Create(alice, b), HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InconsistentExistingPair_Is409AndIsNotSilentlyRepaired(bool owner)
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        var pair = new[] { a, b }.Order().ToArray();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = Db(scope);
        var direct = new Conversation { Type = ConversationType.Direct, DirectUserLowId = pair[0], DirectUserHighId = pair[1] };
        if (owner)
        {
            direct.Members.Add(new ConversationMember { UserId = a, Role = ConversationMemberRole.Owner });
            direct.Members.Add(new ConversationMember { UserId = b });
        }
        db.Conversations.Add(direct); await db.SaveChangesAsync();
        await Problem(await Create(alice, b), HttpStatusCode.Conflict);
        Assert.Equal(owner ? 2 : 0, await db.ConversationMembers.CountAsync(x => x.ConversationId == direct.Id));
    }

    [Fact]
    public async Task FailureOnMembershipInsert_RollsBackConversationAndAllMemberships()
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = Db(scope);
        // Test-only DB constraint forces a failure after the parent INSERT; it is always removed.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" ADD CONSTRAINT \"CK_Test_RejectDirectMember\" CHECK (\"Role\" <> 1) NOT VALID");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Create(alice, b));
            Assert.False(await db.Conversations.AnyAsync(x => x.DirectUserLowId == a || x.DirectUserHighId == a));
            Assert.False(await db.ConversationMembers.AnyAsync(x => x.UserId == a || x.UserId == b));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" DROP CONSTRAINT \"CK_Test_RejectDirectMember\"");
        }
    }
}
