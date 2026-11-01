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

public sealed class ConversationMembershipTests(SessionFixture fixture) : IClassFixture<SessionFixture>
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
    public async Task OwnerAddsAndRemoves_Idempotently_WithoutChangingOwner()
    {
        using var owner = Client(); using var member = Client();
        var a = await Login(owner); var b = await Login(member); var group = await Group(owner);
        Assert.Equal(HttpStatusCode.NoContent, (await Add(owner, group, b)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Add(owner, group, b)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Add(owner, group, a)).StatusCode);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        Assert.Equal(2, await db.ConversationMembers.CountAsync(x => x.ConversationId == group));
        var saved = await db.ConversationMembers.AsNoTracking().SingleAsync(x => x.ConversationId == group && x.UserId == b);
        Assert.Equal(ConversationMemberRole.Member, saved.Role); Assert.Equal(DateTimeKind.Utc, saved.JoinedAt.Kind);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, group, b)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, group, b)).StatusCode);
        Assert.Equal(a, (await db.ConversationMembers.SingleAsync(x => x.ConversationId == group)).UserId);
    }

    [Fact]
    public async Task ConcurrentAddsAndRemoves_PreserveUniqueMembership()
    {
        using var owner = Client(); using var member = Client();
        await Login(owner); var b = await Login(member); var group = await Group(owner);
        var adds = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Add(owner, group, b)));
        Assert.All(adds, x => Assert.Equal(HttpStatusCode.NoContent, x.StatusCode));
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        Assert.Equal(1, await db.ConversationMembers.CountAsync(x => x.ConversationId == group && x.UserId == b));
        var removes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Remove(owner, group, b)));
        Assert.All(removes, x => Assert.Equal(HttpStatusCode.NoContent, x.StatusCode));
        Assert.False(await db.ConversationMembers.AnyAsync(x => x.ConversationId == group && x.UserId == b));
    }

    [Fact]
    public async Task MemberCanLeave_ButCannotManageOthers_AndOwnerCannotLeaveOrBeRemoved()
    {
        using var owner = Client(); using var member = Client(); using var target = Client();
        var a = await Login(owner); var b = await Login(member); var c = await Login(target); var group = await Group(owner, b);
        await Problem(await Add(member, group, c), HttpStatusCode.Forbidden);
        await Problem(await Remove(member, group, a), HttpStatusCode.Forbidden);
        await Problem(await Remove(member, group, b), HttpStatusCode.Forbidden);
        await Problem(await Leave(owner, group), HttpStatusCode.Conflict);
        await Problem(await Remove(owner, group, a), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await Leave(member, group)).StatusCode);
        await Problem(await Leave(member, group), HttpStatusCode.NotFound);
        await Problem(await Add(member, group, c), HttpStatusCode.NotFound);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(a, (await Db(scope).ConversationMembers.SingleAsync(x => x.ConversationId == group)).UserId);
    }

    [Fact]
    public async Task OutsiderAndMissingConversation_Are404_AndCrossConversationIdsCannotMutate()
    {
        using var owner = Client(); using var other = Client(); using var target = Client();
        await Login(owner); await Login(other); var t = await Login(target);
        var group = await Group(owner, t); var otherGroup = await Group(other);
        foreach (var id in new[] { group, Guid.NewGuid() })
        {
            await Problem(await Add(other, id, t), HttpStatusCode.NotFound);
            await Problem(await Remove(other, id, t), HttpStatusCode.NotFound);
            await Problem(await Leave(other, id), HttpStatusCode.NotFound);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(other, otherGroup, t)).StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.True(await Db(scope).ConversationMembers.AnyAsync(x => x.ConversationId == group && x.UserId == t));
    }

    [Fact]
    public async Task DirectMembersCannotUseGroupMutations()
    {
        using var alice = Client(); using var bob = Client();
        await Login(alice); var b = await Login(bob);
        var direct = (await (await Create(alice, b)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id;
        await Problem(await Add(alice, direct, b), HttpStatusCode.Conflict);
        await Problem(await Remove(alice, direct, b), HttpStatusCode.Conflict);
        await Problem(await Leave(alice, direct), HttpStatusCode.Conflict);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(2, await Db(scope).ConversationMembers.CountAsync(x => x.ConversationId == direct));
    }

    [Fact]
    public async Task UnknownAndEmptyTargets_DoNotChangeGroup()
    {
        using var owner = Client(); await Login(owner); var group = await Group(owner);
        await Problem(await Add(owner, group, Guid.NewGuid()), HttpStatusCode.NotFound);
        await Problem(await Add(owner, group, Guid.Empty), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(1, await Db(scope).ConversationMembers.CountAsync(x => x.ConversationId == group));
    }

    [Fact]
    public async Task AnonymousIs401_AndCsrfIsRequiredForAllMutations()
    {
        using var owner = Client(); using var anonymous = Client();
        var a = await Login(owner); var group = await Group(owner);
        foreach (var response in new[] { await Add(anonymous, group, a), await Remove(anonymous, group, a), await Leave(anonymous, group) })
            await Problem(response, HttpStatusCode.Unauthorized);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        foreach (var response in new[] { await Add(owner, group, a), await Remove(owner, group, a), await Leave(owner, group) })
            await Problem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FailedAddRollsBack_AndReleasesLockForNextRequest()
    {
        using var owner = Client(); using var member = Client();
        await Login(owner); var b = await Login(member); var group = await Group(owner);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" ADD CONSTRAINT \"CK_Test_RejectMembership\" CHECK (\"Role\" <> 1) NOT VALID");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Add(owner, group, b));
            Assert.Equal(1, await db.ConversationMembers.CountAsync(x => x.ConversationId == group));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE conversations.\"ConversationMembers\" DROP CONSTRAINT \"CK_Test_RejectMembership\""); }
        Assert.Equal(HttpStatusCode.NoContent, (await Add(owner, group, b)).StatusCode);
    }
}
