using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chat.Api.Conversations;
using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

// Registered only by test factories. Production details/list endpoints belong to #30.
[ApiController]
[Route("test/conversations/{id:guid}")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
public sealed class ConversationReadProbeController(ConversationAuthorization authorization) : ControllerBase
{
    [HttpGet("details")]
    public async Task<IActionResult> Read(Guid id, CancellationToken cancellationToken)
    {
        var access = await authorization.CheckAsync(id, ConversationOperation.Read, cancellationToken: cancellationToken);
        if (!access.Allowed) return Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
        var details = await authorization.ReadableConversations().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.Type }).SingleOrDefaultAsync(cancellationToken);
        return details is null ? Problem(statusCode: 404, title: "Conversation not found.") : Ok(details);
    }

    [HttpGet("decision/{operation:int}")]
    public async Task<IActionResult> Decision(Guid id, int operation, Guid? targetUserId, CancellationToken cancellationToken)
    {
        var access = await authorization.CheckAsync(id, (ConversationOperation)operation, targetUserId, cancellationToken);
        return access.Allowed ? NoContent() : Problem(statusCode: access.ErrorStatus, title: access.ErrorTitle);
    }
}

public sealed class ConversationAuthorizationTests(SessionFixture fixture) : IClassFixture<SessionFixture>
{
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
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

    private WebApplicationFactory<Program> Factory() => fixture.Factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services => services.AddControllers().AddApplicationPart(typeof(ConversationReadProbeController).Assembly)));
    private static async Task<Guid> Group(HttpClient client, params Guid[] members)
    {
        var response = await client.PostAsJsonAsync("/api/conversations/group", new { name = "Protected group", memberIds = members });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GroupConversationResponse>())!.Id;
    }
    private static Task<HttpResponseMessage> Read(HttpClient client, Guid id) => client.GetAsync($"/test/conversations/{id}/details");

    [Fact]
    public async Task GroupReads_RequireMembership_AndCannotBeSpoofedWithIdsOrHeaders()
    {
        using var factory = Factory(); using var owner = Client(factory); using var member = Client(factory);
        using var outsider = Client(factory); using var anonymous = Client(factory);
        var a = await Login(owner); var b = await Login(member); await Login(outsider); var group = await Group(owner, b);
        Assert.Equal(HttpStatusCode.OK, (await Read(owner, group)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Read(member, group)).StatusCode);
        outsider.DefaultRequestHeaders.Add("X-User-Id", a.ToString());
        await Problem(await outsider.GetAsync($"/test/conversations/{group}/details?userId={a}"), HttpStatusCode.NotFound);
        await Problem(await Read(anonymous, group), HttpStatusCode.Unauthorized);
        await Problem(await Read(owner, Guid.NewGuid()), HttpStatusCode.NotFound);
        var otherGroup = await Group(outsider);
        await Problem(await Read(owner, otherGroup), HttpStatusCode.NotFound);
        using var production = Client(fixture.Factory);
        await Login(production);
        Assert.Equal(HttpStatusCode.NotFound, (await production.GetAsync($"/test/conversations/{group}/details")).StatusCode);
    }

    [Fact]
    public async Task DirectReads_AllowOnlyParticipants_AndMembershipMutationsRemainForbidden()
    {
        using var factory = Factory(); using var a = Client(factory); using var b = Client(factory); using var outsider = Client(factory);
        await Login(a); var bid = await Login(b); await Login(outsider);
        var direct = (await (await Create(a, bid)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await Read(a, direct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Read(b, direct)).StatusCode);
        await Problem(await Read(outsider, direct), HttpStatusCode.NotFound);
        foreach (var operation in new[] { ConversationOperation.AddMember, ConversationOperation.RemoveMember, ConversationOperation.Leave })
            await Problem(await a.GetAsync($"/test/conversations/{direct}/decision/{(int)operation}?targetUserId={bid}"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PermissionsMatrix_UsesConversationRoles_AndFailsClosedForUnknownOperations()
    {
        using var factory = Factory(); using var owner = Client(factory); using var member = Client(factory);
        var a = await Login(owner); var b = await Login(member); var group = await Group(owner, b);
        foreach (var operation in new[] { ConversationOperation.AddMember, ConversationOperation.RemoveMember })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await owner.GetAsync($"/test/conversations/{group}/decision/{(int)operation}?targetUserId={b}")).StatusCode);
            await Problem(await member.GetAsync($"/test/conversations/{group}/decision/{(int)operation}?targetUserId={b}"), HttpStatusCode.Forbidden);
        }
        await Problem(await owner.GetAsync($"/test/conversations/{group}/decision/2?targetUserId={a}"), HttpStatusCode.Conflict);
        await Problem(await owner.GetAsync($"/test/conversations/{group}/decision/3"), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await member.GetAsync($"/test/conversations/{group}/decision/3")).StatusCode);
        await Problem(await owner.GetAsync($"/test/conversations/{group}/decision/999"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RemovalAndReAdd_ImmediatelyChangeReadAccess_WithoutCachedPermissions()
    {
        using var factory = Factory(); using var owner = Client(factory); using var member = Client(factory);
        await Login(owner); var b = await Login(member); var group = await Group(owner, b);
        Assert.Equal(HttpStatusCode.OK, (await Read(member, group)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/conversations/{group}/members/{b}")).StatusCode);
        await Problem(await Read(member, group), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync($"/api/conversations/{group}/members/{b}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Read(member, group)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/conversations/{group}/membership")).StatusCode);
        await Problem(await Read(member, group), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeletedCallerAndConversation_AreHandledConsistently()
    {
        using var factory = Factory(); using var owner = Client(factory); using var deleted = Client(factory);
        await Login(owner); var id = await Login(deleted); var group = await Group(owner);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await db.Users.Where(x => x.Id == id).ExecuteDeleteAsync();
        await Problem(await Read(deleted, group), HttpStatusCode.Unauthorized);
        await Problem(await deleted.PutAsync($"/api/conversations/{group}/members/{Guid.NewGuid()}", null), HttpStatusCode.Unauthorized);
        await db.Conversations.Where(x => x.Id == group).ExecuteDeleteAsync();
        await Problem(await Read(owner, group), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ScopedServiceWithoutHttpIdentity_DeniesAndReturnsNoReadableRows()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ConversationAuthorization>();
        var access = await service.CheckAsync(Guid.NewGuid(), ConversationOperation.Read);
        Assert.False(access.Allowed); Assert.Equal(401, access.ErrorStatus);
        Assert.Empty(await service.ReadableConversations().ToListAsync());
    }
}
