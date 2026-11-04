using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chat.Api.Conversations;
using Chat.Api.Messages;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class MessageCreationTests(SessionFixture fixture) : IClassFixture<SessionFixture>
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

    private static Task<HttpResponseMessage> Send(HttpClient client, Guid conversation, string body) =>
        client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { body });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembersSend_AndResponseIsSafeCommittedData(bool direct)
    {
        using var alice = Client(); using var bob = Client();
        var a = await Login(alice); var b = await Login(bob);
        var conversation = direct ? (await (await Create(alice, b)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id : await Group(alice, b);
        const string body = "  Ελληνικά\n<script>plain text</script> 😀  ";
        foreach (var (client, sender) in new[] { (alice, a), (bob, b) })
        {
            var response = await Send(client, conversation, body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "body", "conversationId", "createdAt", "id", "senderId" }, json.EnumerateObject().Select(x => x.Name).Order());
            var dto = json.Deserialize<MessageResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(sender, dto.SenderId); Assert.Equal(body, dto.Body); Assert.Equal(DateTimeKind.Utc, dto.CreatedAt.Kind);
            using var scope = fixture.Factory.Services.CreateScope();
            var saved = await Db(scope).Messages.AsNoTracking().SingleAsync(x => x.Id == dto.Id);
            Assert.Equal(dto.ConversationId, saved.ConversationId); Assert.Equal(dto.Body, saved.Body); Assert.Equal(dto.CreatedAt, saved.CreatedAt);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \t\n")]
    [InlineData("\u00a0\u2003")]
    [InlineData("\v")]
    [InlineData("hello\0world")]
    public async Task InvalidBodyIsRejectedWithoutWrites(string body)
    {
        using var client = Client(); await Login(client); var conversation = await Group(client);
        await Problem(await Send(client, conversation, body), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).Messages.AnyAsync(x => x.ConversationId == conversation));
    }

    [Fact]
    public async Task BodyLimitsAndClientForgedFieldsAreRejected()
    {
        using var client = Client(); await Login(client); var conversation = await Group(client);
        await Problem(await Send(client, conversation, new string('x', 4001)), HttpStatusCode.BadRequest);
        await Problem(await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { body = "forged", senderId = Guid.NewGuid() }), HttpStatusCode.BadRequest);
        await Problem(await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { body = "forged", id = Guid.NewGuid(), createdAt = DateTime.UtcNow }), HttpStatusCode.BadRequest);
        await Problem(await client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { }), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Created, (await Send(client, conversation, new string('x', 4000))).StatusCode);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(1, await Db(scope).Messages.CountAsync(x => x.ConversationId == conversation));
    }

    [Fact]
    public async Task OutsiderAnonymousAndMissingResourcesCannotSend()
    {
        using var owner = Client(); using var outsider = Client(); using var anonymous = Client();
        await Login(owner); await Login(outsider); var conversation = await Group(owner);
        await Problem(await Send(outsider, conversation, "attack"), HttpStatusCode.NotFound);
        await Problem(await Send(anonymous, conversation, "attack"), HttpStatusCode.Unauthorized);
        await Problem(await Send(owner, Guid.NewGuid(), "missing"), HttpStatusCode.NotFound);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await db.Conversations.Where(x => x.Id == conversation).ExecuteDeleteAsync();
        await Problem(await Send(owner, conversation, "deleted"), HttpStatusCode.NotFound);
        Assert.False(await db.Messages.AnyAsync(x => x.ConversationId == conversation));
    }

    [Fact]
    public async Task CsrfAndDeletedIdentityAreRejected()
    {
        using var owner = Client(); using var deleted = Client();
        await Login(owner); var id = await Login(deleted); var conversation = await Group(owner);
        owner.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Problem(await Send(owner, conversation, "csrf"), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Users.Where(x => x.Id == id).ExecuteDeleteAsync();
        await Problem(await Send(deleted, conversation, "deleted"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SendWaitingForLock_RechecksMembershipAfterConcurrentRemoval()
    {
        using var owner = Client(); using var member = Client();
        await Login(owner); var id = await Login(member); var conversation = await Group(owner, id);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Conversations.FromSqlInterpolated($"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleAsync();
        var pending = Send(member, conversation, "too late");
        using var observerScope = fixture.Factory.Services.CreateScope();
        var observer = Db(observerScope);
        var waiting = false;
        for (var attempt = 0; attempt < 100 && !waiting; attempt++)
        {
            waiting = await observer.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync() > 0;
            if (!waiting) await Task.Delay(50);
        }
        Assert.True(waiting, "The send must actually wait for the conversation row lock.");
        await db.ConversationMembers.Where(x => x.ConversationId == conversation && x.UserId == id).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        await Problem(await pending, HttpStatusCode.NotFound);
        Assert.False(await db.Messages.AnyAsync(x => x.ConversationId == conversation));
    }

    [Fact]
    public async Task DatabaseFailureRollsBackAndReleasesConversationLock()
    {
        using var client = Client(); await Login(client); var conversation = await Group(client);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE messages.\"Messages\" ADD CONSTRAINT \"CK_Test_RejectSend\" CHECK (\"Body\" <> 'reject-test') NOT VALID");
        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => Send(client, conversation, "reject-test"));
            Assert.False(await db.Messages.AnyAsync(x => x.ConversationId == conversation));
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE messages.\"Messages\" DROP CONSTRAINT \"CK_Test_RejectSend\""); }
        Assert.Equal(HttpStatusCode.Created, (await Send(client, conversation, "works")).StatusCode);
    }
}
