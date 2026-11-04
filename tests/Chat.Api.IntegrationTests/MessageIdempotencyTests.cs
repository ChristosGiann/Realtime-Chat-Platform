using System.Net;
using System.Net.Http.Json;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private static Task<HttpResponseMessage> SendKey(HttpClient client, Guid conversation, Guid key, string body = "Hello") =>
        client.PostAsJsonAsync($"/api/conversations/{conversation}/messages", new { clientMessageId = key, body });

    [Fact]
    public async Task SequentialAndConcurrentRetriesReturnOriginalCommittedMessage()
    {
        using var client = Client(); await Login(client); var conversation = await Group(client); var key = Guid.NewGuid();
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => SendKey(client, conversation, key)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(11, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        var messages = await Task.WhenAll(responses.Select(x => x.Content.ReadFromJsonAsync<MessageResponse>()));
        var original = messages[0];
        Assert.All(messages, message => Assert.Equal(original, message));
        var retry = await SendKey(client, conversation, key);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(original, await retry.Content.ReadFromJsonAsync<MessageResponse>());
        Assert.Equal(key, original!.ClientMessageId);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(1, await Db(scope).Messages.CountAsync(x => x.ClientMessageId == key));
        Assert.Equal(HttpStatusCode.Created, (await SendKey(client, conversation, Guid.NewGuid())).StatusCode);
        Assert.Equal(2, await Db(scope).Messages.CountAsync(x => x.ConversationId == conversation));
    }

    [Fact]
    public async Task KeysAreBoundToExactContentSenderAndConversation_WithoutLeakingOriginal()
    {
        using var alice = Client(); using var bob = Client(); await Login(alice); var b = await Login(bob);
        var conversation = await Group(alice, b); var other = await Group(alice); var key = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await SendKey(alice, conversation, key)).StatusCode);
        foreach (var body in new[] { "hello", "Hello ", "Different" })
            await Problem(await SendKey(alice, conversation, key, body), HttpStatusCode.Conflict);
        await Problem(await SendKey(alice, other, key), HttpStatusCode.Conflict);
        var conflict = await SendKey(bob, conversation, key);
        await Problem(conflict, HttpStatusCode.Conflict);
        Assert.DoesNotContain("senderId", await conflict.Content.ReadAsStringAsync());
        var memberKey = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await SendKey(bob, conversation, memberKey)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(alice, conversation, b)).StatusCode);
        await Problem(await SendKey(bob, conversation, memberKey), HttpStatusCode.NotFound);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(1, await Db(scope).Messages.CountAsync(x => x.ClientMessageId == key));
    }

    [Fact]
    public async Task ConcurrentDifferentConversationsCannotReuseKey()
    {
        using var client = Client(); await Login(client);
        var conversations = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Group(client)));
        var key = Guid.NewGuid();
        var responses = await Task.WhenAll(conversations.Select(x => SendKey(client, x, key)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(5, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.Equal(1, await Db(scope).Messages.CountAsync(x => x.ClientMessageId == key));
    }

    [Theory]
    [InlineData("{\"body\":\"Hello\"}")]
    [InlineData("{\"body\":\"Hello\",\"clientMessageId\":null}")]
    [InlineData("{\"body\":\"Hello\",\"clientMessageId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"body\":\"Hello\",\"clientMessageId\":\"invalid\"}")]
    public async Task InvalidKeysCannotWrite(string json)
    {
        using var client = Client(); await Login(client); var conversation = await Group(client);
        await Problem(await client.PostAsync($"/api/conversations/{conversation}/messages",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        using var scope = fixture.Factory.Services.CreateScope();
        Assert.False(await Db(scope).Messages.AnyAsync(x => x.ConversationId == conversation));
    }

    [Fact]
    public async Task PostgreSqlRejectsDuplicateAndEmptyKeysWithoutController()
    {
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        var seed = await MessagePersistenceTests.Seed(db); var key = Guid.NewGuid();
        db.Messages.Add(new Message { ConversationId = seed.Conversation, SenderId = seed.User, ClientMessageId = key, Body = "Original" });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        foreach (var (invalidKey, code) in new[] { (key, PostgresErrorCodes.UniqueViolation), (Guid.Empty, PostgresErrorCodes.CheckViolation) })
        {
            db.Messages.Add(new Message { ConversationId = seed.Conversation, SenderId = seed.User, ClientMessageId = invalidKey, Body = "Other" });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(code, Assert.IsType<PostgresException>(error.InnerException).SqlState);
            db.ChangeTracker.Clear();
        }
    }
}
