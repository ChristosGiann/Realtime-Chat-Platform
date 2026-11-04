using System.Net;
using System.Net.Http.Json;
using Chat.Api.Conversations;
using Chat.Api.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private static string HistoryUrl(Guid conversation, int limit = 30, string? cursor = null) =>
        $"/api/conversations/{conversation}/messages?limit={limit}" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryTraversesTimestampTiesAndKeepsSafeProfiles(bool direct)
    {
        using var owner = Client(); using var member = Client(); var sender = await Login(owner); var memberId = await Login(member);
        var conversation = direct ? (await (await Create(owner, memberId)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id : await Group(owner, memberId);
        using var scope = fixture.Factory.Services.CreateScope(); var db = Db(scope);
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        db.Messages.AddRange(Enumerable.Range(0, 7).Select(i => new Message { ConversationId = conversation, SenderId = sender, Body = $"Message {i}", CreatedAt = timestamp }));
        await db.SaveChangesAsync();
        var expected = await db.Messages.Where(x => x.ConversationId == conversation).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Select(x => x.Id).ToListAsync();
        var ids = new List<Guid>(); string? cursor = null;
        do
        {
            var response = await member.GetAsync(HistoryUrl(conversation, 2, cursor));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
            var page = System.Text.Json.JsonSerializer.Deserialize<MessageHistoryPage>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
            Assert.InRange(page.Items.Count, 1, 2);
            Assert.All(page.Items, x => { Assert.Equal(sender, x.Sender.UserId); Assert.NotNull(x.Sender.DisplayName); });
            ids.AddRange(page.Items.Select(x => x.Id)); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(expected, ids); Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task HistoryProtectsContextAndChecksCurrentMembership()
    {
        using var owner = Client(); using var member = Client(); using var outsider = Client(); using var anonymous = Client();
        await Login(owner); var memberId = await Login(member); await Login(outsider);
        var conversation = await Group(owner, memberId); var other = await Group(owner);
        await Send(owner, conversation, "one"); await Send(owner, conversation, "two");
        var page = (await owner.GetFromJsonAsync<MessageHistoryPage>(HistoryUrl(conversation, 1)))!;
        Assert.NotNull(page.NextCursor);
        foreach (var url in new[] { HistoryUrl(other, 1, page.NextCursor), HistoryUrl(conversation, 2, page.NextCursor), HistoryUrl(conversation, 1, "tampered"), HistoryUrl(conversation, 0), HistoryUrl(conversation, 101) })
            await Problem(await owner.GetAsync(url), HttpStatusCode.BadRequest);
        await Problem(await member.GetAsync(HistoryUrl(conversation, 1, page.NextCursor)), HttpStatusCode.BadRequest);
        await Problem(await outsider.GetAsync(HistoryUrl(conversation)), HttpStatusCode.NotFound);
        await Problem(await anonymous.GetAsync(HistoryUrl(conversation)), HttpStatusCode.Unauthorized);
        await Remove(owner, conversation, memberId);
        await Problem(await member.GetAsync(HistoryUrl(conversation)), HttpStatusCode.NotFound);
        await Problem(await owner.GetAsync(HistoryUrl(Guid.NewGuid())), HttpStatusCode.NotFound);
        var empty = (await owner.GetFromJsonAsync<MessageHistoryPage>(HistoryUrl(other)))!;
        Assert.Empty(empty.Items); Assert.Null(empty.NextCursor);
    }

    [Fact]
    public async Task HistoryCursorSurvivesDeletedAnchorAndExcludesNewerMessages()
    {
        using var client = Client(); await Login(client); var conversation = await Group(client);
        await Send(client, conversation, "old"); await Send(client, conversation, "anchor");
        var first = (await client.GetFromJsonAsync<MessageHistoryPage>(HistoryUrl(conversation, 1)))!;
        using var scope = fixture.Factory.Services.CreateScope();
        await Db(scope).Messages.Where(x => x.Id == first.Items[0].Id).ExecuteDeleteAsync();
        await Send(client, conversation, "new");
        var next = (await client.GetFromJsonAsync<MessageHistoryPage>(HistoryUrl(conversation, 1, first.NextCursor)))!;
        Assert.Equal("old", Assert.Single(next.Items).Body); Assert.Null(next.NextCursor);
    }
}
