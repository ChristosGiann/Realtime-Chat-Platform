using System.Net;
using System.Net.Http.Json;
using Chat.Api.AiSummaries;
using Chat.Api.Conversations;
using Chat.Api.Messages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private sealed class FakeSummaryGenerator : IUnreadSummaryGenerator
    {
        public List<SummaryPromptMessage[]> Calls { get; } = [];
        public TaskCompletionSource? Entered { get; set; }
        public TaskCompletionSource? Release { get; set; }
        public bool Fail { get; set; }
        public bool Malformed { get; set; }

        public async Task<SummaryContent> GenerateAsync(IReadOnlyList<SummaryPromptMessage> messages, CancellationToken cancellationToken)
        {
            Calls.Add(messages.ToArray());
            Entered?.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new HttpRequestException("Local model unavailable.");
            if (Malformed) return new("", [], [], []);
            return new("Η ομάδα συζήτησε το θέμα.", ["Σημαντικό σημείο"], [], []);
        }
        public Task<SummaryContent> SynthesizeAsync(IReadOnlyList<SummaryContent> chunks, CancellationToken cancellationToken) =>
            Task.FromResult(new SummaryContent("Συνολική περίληψη.", ["Σημαντικό σημείο"], [], []));
    }

    private WebApplicationFactory<Program> SummaryFactory(FakeSummaryGenerator fake, int chunkSize = 50, int maxMessages = 200,
        int timeoutSeconds = 60, bool enabled = true) =>
        fixture.Factory.WithWebHostBuilder(builder => builder
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UnreadSummaries:Enabled"] = enabled.ToString(), ["UnreadSummaries:ChunkSize"] = chunkSize.ToString(),
                ["UnreadSummaries:MaxMessages"] = maxMessages.ToString(), ["UnreadSummaries:TimeoutSeconds"] = timeoutSeconds.ToString()
            }))
            .ConfigureTestServices(services =>
            {
                services.RemoveAll<IUnreadSummaryGenerator>();
                services.AddSingleton<IUnreadSummaryGenerator>(fake);
            }));

    private static string SummaryUrl(Guid conversation) => $"/api/conversations/{conversation}/unread-summary";
    private static HttpClient SummaryClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    [Fact]
    public async Task SummaryRequiresMemberAndGroupAndDoesNotChangeReadOrDeliveryProgress()
    {
        var fake = new FakeSummaryGenerator(); using var factory = SummaryFactory(fake);
        using var owner = SummaryClient(factory); using var member = SummaryClient(factory);
        using var outsider = SummaryClient(factory); using var anonymous = SummaryClient(factory);
        await Login(owner); var memberId = await Login(member); await Login(outsider);
        var group = await Group(owner, memberId);
        var direct = (await (await Create(owner, memberId)).Content.ReadFromJsonAsync<DirectConversationResponse>())!.Id;
        await Problem(await anonymous.PostAsync(SummaryUrl(group), null), HttpStatusCode.Unauthorized);
        await Problem(await outsider.PostAsync(SummaryUrl(group), null), HttpStatusCode.NotFound);
        await Problem(await member.PostAsync(SummaryUrl(direct), null), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await member.PostAsync(SummaryUrl(group), null)).StatusCode);
        var first = (await (await Send(owner, group, "Πρώτο")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var second = (await (await Send(owner, group, "Δεύτερο")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var before = (await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(group)))!;
        var result = await member.PostAsync(SummaryUrl(group), null);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var summary = (await result.Content.ReadFromJsonAsync<UnreadSummaryResponse>())!;
        Assert.Null(summary.FromReadMessageId); Assert.Equal(second.Id, summary.ThroughMessageId);
        Assert.Equal(2, summary.UnreadMessagesSummarized);
        Assert.Equal(before, await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(group)));
        Assert.Equal(new[] { "Πρώτο", "Δεύτερο" }, fake.Calls.Single().Select(x => x.Text));
        using var scope = factory.Services.CreateScope();
        var db = Db(scope);
        var progress = await db.ConversationMembers.AsNoTracking().SingleAsync(x => x.ConversationId == group && x.UserId == memberId);
        Assert.Null(progress.LastReadMessageId); Assert.Null(progress.LastDeliveredMessageId);
        Assert.Equal("Μέλος 1", fake.Calls.Single()[0].Sender);
    }

    [Fact]
    public async Task SnapshotExcludesNewerMessagesAndInferenceDoesNotHoldConversationLock()
    {
        var fake = new FakeSummaryGenerator { Entered = new(), Release = new() };
        using var factory = SummaryFactory(fake); using var owner = SummaryClient(factory); using var member = SummaryClient(factory);
        await Login(owner); var memberId = await Login(member); var group = await Group(owner, memberId);
        var first = (await (await Send(owner, group, "before")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var pending = member.PostAsync(SummaryUrl(group), null);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Send needs the same PostgreSQL conversation FOR UPDATE lock. It must finish while the fake LLM is blocked.
        var newer = await Send(owner, group, "after").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Created, newer.StatusCode);
        fake.Release.TrySetResult();
        var result = (await (await pending).Content.ReadFromJsonAsync<UnreadSummaryResponse>())!;
        Assert.Equal(first.Id, result.ThroughMessageId);
        Assert.Equal(1, result.UnreadMessagesSummarized);
        Assert.Single(fake.Calls.Single());
        Assert.Equal(2, (await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(group)))!.UnreadCount);
    }

    [Fact]
    public async Task RevokedMembershipDiscardsBlockedInference()
    {
        var fake = new FakeSummaryGenerator { Entered = new(), Release = new() };
        using var factory = SummaryFactory(fake); using var owner = SummaryClient(factory); using var member = SummaryClient(factory);
        await Login(owner); var memberId = await Login(member); var group = await Group(owner, memberId);
        await Send(owner, group, "before");
        var pending = member.PostAsync(SummaryUrl(group), null);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, group, memberId)).StatusCode);
        fake.Release.TrySetResult();
        await Problem(await pending, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChunkingAndLimitAreDeterministic()
    {
        var fake = new FakeSummaryGenerator(); using var factory = SummaryFactory(fake, chunkSize: 2, maxMessages: 4);
        using var owner = SummaryClient(factory); using var member = SummaryClient(factory);
        await Login(owner); var memberId = await Login(member); var group = await Group(owner, memberId);
        for (var i = 0; i < 4; i++) await Send(owner, group, $"message {i}");
        var summary = (await (await member.PostAsync(SummaryUrl(group), null)).Content.ReadFromJsonAsync<UnreadSummaryResponse>())!;
        Assert.Equal(4, summary.UnreadMessagesSummarized);
        Assert.Equal(2, fake.Calls.Count);
        Assert.All(fake.Calls, chunk => Assert.Equal(2, chunk.Length));
        await Send(owner, group, "fifth");
        await Problem(await member.PostAsync(SummaryUrl(group), null), (HttpStatusCode)422);
        Assert.Equal(2, fake.Calls.Count);
    }

    [Fact]
    public async Task ReadAnchorDefinesRangeAndSummaryNeverAdvancesIt()
    {
        var fake = new FakeSummaryGenerator(); using var factory = SummaryFactory(fake);
        using var owner = SummaryClient(factory); using var member = SummaryClient(factory);
        await Login(owner); var memberId = await Login(member); var group = await Group(owner, memberId);
        var first = (await (await Send(owner, group, "already read")).Content.ReadFromJsonAsync<MessageResponse>())!;
        await member.PutAsJsonAsync(ReadUrl(group), new { messageId = first.Id });
        var second = (await (await Send(owner, group, "new unread")).Content.ReadFromJsonAsync<MessageResponse>())!;
        var before = (await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(group)))!;
        var summary = (await (await member.PostAsync(SummaryUrl(group), null)).Content.ReadFromJsonAsync<UnreadSummaryResponse>())!;
        Assert.Equal(first.Id, summary.FromReadMessageId);
        Assert.Equal(second.Id, summary.ThroughMessageId);
        Assert.Equal("new unread", Assert.Single(fake.Calls.Single()).Text);
        Assert.Equal(before, await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(group)));
    }

    [Fact]
    public async Task ModelOutageIsNonFatalAndCurrentMembershipIsRequiredAfterRejoin()
    {
        var fake = new FakeSummaryGenerator { Fail = true };
        using var factory = SummaryFactory(fake); using var owner = SummaryClient(factory); using var member = SummaryClient(factory);
        await Login(owner); var memberId = await Login(member); var group = await Group(owner, memberId);
        await Send(owner, group, "unread");
        await Problem(await member.PostAsync(SummaryUrl(group), null), HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, (await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(group)))!.UnreadCount);
        fake.Fail = false; fake.Entered = new(); fake.Release = new();
        var pending = member.PostAsync(SummaryUrl(group), null);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.NoContent, (await Remove(owner, group, memberId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Add(owner, group, memberId)).StatusCode);
        fake.Release.TrySetResult();
        await Problem(await pending, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InferenceTimeoutIsNonFatalAndDoesNotAdvanceProgress()
    {
        var fake = new FakeSummaryGenerator { Entered = new(), Release = new() };
        using var factory = SummaryFactory(fake, timeoutSeconds: 1);
        using var owner = SummaryClient(factory); using var member = SummaryClient(factory);
        await Login(owner); var memberId = await Login(member); var group = await Group(owner, memberId);
        await Send(owner, group, "unread");
        await Problem(await member.PostAsync(SummaryUrl(group), null), HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, (await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(group)))!.UnreadCount);
    }

    [Fact]
    public async Task DisabledAndMalformedSummaryAreNonFatalAndAuthorizationStillApplies()
    {
        var fake = new FakeSummaryGenerator(); using var disabledFactory = SummaryFactory(fake, enabled: false);
        using var owner = SummaryClient(disabledFactory); using var outsider = SummaryClient(disabledFactory);
        await Login(owner); await Login(outsider); var group = await Group(owner);
        await Send(owner, group, "own message");
        await Problem(await outsider.PostAsync(SummaryUrl(group), null), HttpStatusCode.NotFound);
        await Problem(await owner.PostAsync(SummaryUrl(group), null), HttpStatusCode.ServiceUnavailable);
        Assert.Empty(fake.Calls);

        fake.Malformed = true;
        using var enabledFactory = SummaryFactory(fake);
        using var member = SummaryClient(enabledFactory); using var enabledOwner = SummaryClient(enabledFactory);
        await Login(enabledOwner); var memberId = await Login(member); var otherGroup = await Group(enabledOwner, memberId);
        await Send(enabledOwner, otherGroup, "unread");
        await Problem(await member.PostAsync(SummaryUrl(otherGroup), null), HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, (await member.GetFromJsonAsync<ReadStateResponse>(ReadUrl(otherGroup)))!.UnreadCount);
    }
}
