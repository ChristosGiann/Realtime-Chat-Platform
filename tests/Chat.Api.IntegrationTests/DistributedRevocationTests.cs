using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Collections.Concurrent;
using Chat.Api.Messages;
using Chat.Api.Realtime;
using Chat.Api.Infrastructure.Redis;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Chat.Api.IntegrationTests;

public sealed class DistributedRevocationTests(MultiInstanceTopology topology) : IClassFixture<MultiInstanceTopology>
{
    private static HttpClient Client(Uri url) => new() { BaseAddress = url };
    private static async Task<(Guid Id, string Cookie)> Login(HttpClient client)
    {
        var name = Guid.NewGuid().ToString("N"); var email = $"{name}@example.test";
        var created = await client.PostAsJsonAsync("/api/auth/register",
            new { email, userName = name, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Csrf(client);
        var signed = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.NoContent, signed.StatusCode);
        var cookie = Assert.Single(signed.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Chat.Session=")).Split(';')[0];
        await Csrf(client);
        return (id, cookie);
    }
    private static async Task Csrf(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf")).GetProperty("requestToken").GetString();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    private static HubConnection Hub(Uri url, string cookie) => new HubConnectionBuilder()
        .WithUrl(new Uri(url, "/hubs/chat"), options => options.Headers.Add("Cookie", cookie)).Build();
    private async Task Probe(HubConnection target)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = target.On("RevocationProbe", () => ready.TrySetResult());
        var hub = topology.A.Services.GetRequiredService<IHubContext<ChatHub>>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!ready.Task.IsCompleted)
        {
            await hub.Clients.Client(target.ConnectionId!).SendAsync("RevocationProbe", cancellationToken: timeout.Token);
            await Task.WhenAny(ready.Task, Task.Delay(100, timeout.Token));
            timeout.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task<(HttpClient Owner, Guid Conversation, Guid Member)> Scenario()
    {
        var owner = Client(topology.AUrl); using var memberHttp = Client(topology.BUrl);
        await Login(owner); var member = await Login(memberHttp);
        var created = await owner.PostAsJsonAsync("/api/conversations/group",
            new { name = "Revocation failure", memberIds = new[] { member.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (owner, conversation, member.Id);
    }

    private async Task<(IDatabase Db, string Prefix, string Owner)> FakeOwner()
    {
        var db = (await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync()).GetDatabase();
        var prefix = topology.A.Services.GetRequiredService<RedisNamespace>().CoordinationPrefix;
        var epoch = await topology.A.Services.GetRequiredService<DistributedConnectionRegistry>().RequireEpoch(CancellationToken.None);
        var owner = "unresponsive:" + Guid.NewGuid().ToString("N");
        await db.StringSetAsync(prefix + "owner:" + owner, epoch, TimeSpan.FromSeconds(15));
        await db.SortedSetAddAsync(prefix + "owners", owner, DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds());
        return (db, prefix, owner);
    }
    private static async Task RemoveFake(IDatabase db, string prefix, string owner)
    {
        await db.KeyDeleteAsync(prefix + "owner:" + owner);
        await db.SortedSetRemoveAsync(prefix + "owners", owner);
    }

    [Fact]
    public async Task RemovalFencesTabsOnBothInstancesAndRequiresFreshSubscribeAfterRejoin()
    {
        using var owner = Client(topology.AUrl); using var memberHttp = Client(topology.BUrl);
        var ownerUser = await Login(owner); var member = await Login(memberHttp);
        var created = await owner.PostAsJsonAsync("/api/conversations/group",
            new { name = "Revocation", memberIds = new[] { member.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Guid originalMembership;
        using (var scope = topology.A.Services.CreateScope())
            originalMembership = await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .Where(x => x.ConversationId == conversation && x.UserId == member.Id)
                .Select(x => x.MembershipId).SingleAsync();
        await using var tabA = Hub(topology.AUrl, member.Cookie);
        await using var tabB = Hub(topology.BUrl, member.Cookie);
        await tabA.StartAsync(); await tabB.StartAsync();
        await tabA.InvokeAsync("Subscribe", conversation); await tabB.InvokeAsync("Subscribe", conversation);
        var captured = await topology.A.Services.GetRequiredService<DistributedConnectionRegistry>()
            .SnapshotRevocation(conversation, member.Id, originalMembership, CancellationToken.None);
        var removed = await owner.DeleteAsync($"/api/conversations/{conversation}/members/{member.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => tabA.InvokeAsync("GetTyping", conversation));
        await Assert.ThrowsAnyAsync<Exception>(() => tabB.InvokeAsync("GetTyping", conversation));
        var rejoined = await owner.PutAsync($"/api/conversations/{conversation}/members/{member.Id}", null);
        Assert.Equal(HttpStatusCode.NoContent, rejoined.StatusCode);
        using (var scope = topology.A.Services.CreateScope())
        {
            var fresh = await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .Where(x => x.ConversationId == conversation && x.UserId == member.Id).SingleAsync();
            Assert.NotEqual(originalMembership, fresh.MembershipId);
            Assert.Null(fresh.LastDeliveredMessageId);
            Assert.Null(fresh.LastReadMessageId);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => tabA.InvokeAsync("GetTyping", conversation));
        await Assert.ThrowsAnyAsync<Exception>(() => tabB.InvokeAsync("GetTyping", conversation));
        await tabA.InvokeAsync("Subscribe", conversation); await tabB.InvokeAsync("Subscribe", conversation);
        Assert.Empty((await tabA.InvokeAsync<TypingSnapshot>("GetTyping", conversation)).Items);
        Assert.Empty((await tabB.InvokeAsync<TypingSnapshot>("GetTyping", conversation)).Items);
        var redis = await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync();
        var db = redis.GetDatabase();
        var prefix = topology.A.Services.GetRequiredService<RedisNamespace>().CoordinationPrefix;
        var replayId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds();
        await db.StringSetAsync(prefix + $"revoke-operation:{replayId:N}",
            $"{originalMembership:N}:{captured.Epoch}:{deadline}", TimeSpan.FromSeconds(30));
        foreach (var ownerId in captured.Owners)
        {
            var oldCommand = new RevocationCommand(replayId, conversation, member.Id, originalMembership,
                captured.Epoch, ownerId, deadline, captured.Connections.Where(x => x.Owner == ownerId).ToArray());
            await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(prefix + "revoke-command:" + ownerId),
                JsonSerializer.Serialize(oldCommand));
        }
        using var replayTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        foreach (var ownerId in captured.Owners)
        {
            RedisValue ack;
            do
            {
                ack = await db.StringGetAsync(prefix + $"revoke-ack:{replayId:N}:{ownerId}");
                if (ack.IsNull) await Task.Delay(30, replayTimeout.Token);
            } while (ack.IsNull);
            Assert.Equal("ACK", (string?)ack);
        }
        Assert.Empty((await tabA.InvokeAsync<TypingSnapshot>("GetTyping", conversation)).Items);
        Assert.Empty((await tabB.InvokeAsync<TypingSnapshot>("GetTyping", conversation)).Items);
    }

    [Fact]
    public async Task MissingOwnerAckReturns503AndRollsBackMembership()
    {
        var scenario = await Scenario(); using var owner = scenario.Owner;
        var fake = await FakeOwner();
        try
        {
            var result = await owner.DeleteAsync($"/api/conversations/{scenario.Conversation}/members/{scenario.Member}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            using var scope = topology.A.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .AnyAsync(x => x.ConversationId == scenario.Conversation && x.UserId == scenario.Member));
        }
        finally { await RemoveFake(fake.Db, fake.Prefix, fake.Owner); }
    }

    [Fact]
    public async Task OwnerDisappearsDuringBarrierAndMembershipStillRollsBack()
    {
        var scenario = await Scenario(); using var owner = scenario.Owner;
        var fake = await FakeOwner();
        var subscriber = (await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync()).GetSubscriber();
        var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(fake.Prefix + "revoke-command:" + fake.Owner));
        var commandSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.OnMessage(async _ =>
        {
            await RemoveFake(fake.Db, fake.Prefix, fake.Owner);
            commandSeen.TrySetResult();
        });
        try
        {
            var pending = owner.DeleteAsync($"/api/conversations/{scenario.Conversation}/members/{scenario.Member}");
            await commandSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await pending).StatusCode);
            using var scope = topology.A.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .AnyAsync(x => x.ConversationId == scenario.Conversation && x.UserId == scenario.Member));
        }
        finally { await queue.UnsubscribeAsync(); await RemoveFake(fake.Db, fake.Prefix, fake.Owner); }
    }

    [Fact]
    public async Task ExplicitOwnerNackReturns503AndRollsBackMembership()
    {
        var scenario = await Scenario(); using var owner = scenario.Owner;
        var fake = await FakeOwner();
        var subscriber = (await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync()).GetSubscriber();
        var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(fake.Prefix + "revoke-command:" + fake.Owner));
        queue.OnMessage(async message =>
        {
            var command = JsonSerializer.Deserialize<RevocationCommand>((string)message.Message!);
            if (command is not null)
                await fake.Db.StringSetAsync(fake.Prefix + $"revoke-ack:{command.OperationId:N}:{fake.Owner}",
                    "NACK", TimeSpan.FromSeconds(30));
        });
        try
        {
            var result = await owner.DeleteAsync($"/api/conversations/{scenario.Conversation}/members/{scenario.Member}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            using var scope = topology.A.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .AnyAsync(x => x.ConversationId == scenario.Conversation && x.UserId == scenario.Member));
        }
        finally { await queue.UnsubscribeAsync(); await RemoveFake(fake.Db, fake.Prefix, fake.Owner); }
    }

    [Fact]
    public async Task LateAckCannotSatisfyOldOrNewOperation()
    {
        var scenario = await Scenario(); using var owner = scenario.Owner;
        var fake = await FakeOwner();
        var subscriber = (await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync()).GetSubscriber();
        var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(fake.Prefix + "revoke-command:" + fake.Owner));
        var seen = new TaskCompletionSource<RevocationCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.OnMessage(message =>
        {
            var command = JsonSerializer.Deserialize<RevocationCommand>((string)message.Message!);
            if (command is not null) seen.TrySetResult(command);
        });
        try
        {
            var first = await owner.DeleteAsync($"/api/conversations/{scenario.Conversation}/members/{scenario.Member}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
            var old = await seen.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await fake.Db.StringSetAsync(fake.Prefix + $"revoke-ack:{old.OperationId:N}:{fake.Owner}",
                "ACK", TimeSpan.FromSeconds(30));
            await fake.Db.StringSetAsync(fake.Prefix + "owner:" + fake.Owner, old.Epoch, TimeSpan.FromSeconds(15));
            await fake.Db.SortedSetAddAsync(fake.Prefix + "owners", fake.Owner,
                DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds());
            var retry = await owner.DeleteAsync($"/api/conversations/{scenario.Conversation}/members/{scenario.Member}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
            using var scope = topology.A.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .AnyAsync(x => x.ConversationId == scenario.Conversation && x.UserId == scenario.Member));
        }
        finally { await queue.UnsubscribeAsync(); await RemoveFake(fake.Db, fake.Prefix, fake.Owner); }
    }

    [Fact]
    public async Task CoordinationEpochChangeDuringBarrierRollsBack()
    {
        var scenario = await Scenario(); using var owner = scenario.Owner;
        var fake = await FakeOwner();
        var subscriber = (await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync()).GetSubscriber();
        var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(fake.Prefix + "revoke-command:" + fake.Owner));
        queue.OnMessage(async _ => await fake.Db.StringSetAsync(fake.Prefix + "epoch", Guid.NewGuid().ToString("N")));
        try
        {
            var result = await owner.DeleteAsync($"/api/conversations/{scenario.Conversation}/members/{scenario.Member}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            using var scope = topology.A.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .AnyAsync(x => x.ConversationId == scenario.Conversation && x.UserId == scenario.Member));
        }
        finally { await queue.UnsubscribeAsync(); await RemoveFake(fake.Db, fake.Prefix, fake.Owner); }
    }

    [Fact]
    public async Task CommitFailureAfterAckLeavesOldGenerationFencedButMembershipIntact()
    {
        using var owner = Client(topology.AUrl); using var memberHttp = Client(topology.BUrl);
        await Login(owner); var member = await Login(memberHttp);
        var created = await owner.PostAsJsonAsync("/api/conversations/group",
            new { name = "Commit failure", memberIds = new[] { member.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var tab = Hub(topology.BUrl, member.Cookie);
        await tab.StartAsync(); await tab.InvokeAsync("Subscribe", conversation);
        using var setupScope = topology.A.Services.CreateScope();
        var setup = setupScope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION conversations.fail_revocation_commit() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'forced revocation commit failure'; END; $$;
            CREATE CONSTRAINT TRIGGER test_fail_revocation_commit
            AFTER DELETE ON conversations."ConversationMembers"
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
            EXECUTE FUNCTION conversations.fail_revocation_commit();
            """);
        try
        {
            var result = await owner.DeleteAsync($"/api/conversations/{conversation}/members/{member.Id}");
            Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
            using var scope = topology.A.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .AnyAsync(x => x.ConversationId == conversation && x.UserId == member.Id));
            await Assert.ThrowsAnyAsync<Exception>(() => tab.InvokeAsync("GetTyping", conversation));
            await tab.InvokeAsync("Subscribe", conversation);
            Assert.Empty((await tab.InvokeAsync<TypingSnapshot>("GetTyping", conversation)).Items);
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER test_fail_revocation_commit ON conversations.\"ConversationMembers\"");
            await setup.Database.ExecuteSqlRawAsync("DROP FUNCTION conversations.fail_revocation_commit()");
        }
    }

    [Fact]
    public async Task RemovalWinsQueuedSendReceiptAndTypingUnderConversationLock()
    {
        using var owner = Client(topology.AUrl); using var memberHttp = Client(topology.BUrl);
        await Login(owner); var member = await Login(memberHttp);
        var created = await owner.PostAsJsonAsync("/api/conversations/group",
            new { name = "Race ordering", memberIds = new[] { member.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var first = await owner.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { body = "before race", clientMessageId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var messageId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var tab = Hub(topology.BUrl, member.Cookie);
        await tab.StartAsync(); await tab.InvokeAsync("Subscribe", conversation);
        using var scope = topology.A.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await using var hold = await db.Database.BeginTransactionAsync();
        await db.Conversations.FromSqlInterpolated(
            $"SELECT * FROM conversations.\"Conversations\" WHERE \"Id\" = {conversation} FOR UPDATE").SingleAsync();
        var removal = owner.DeleteAsync($"/api/conversations/{conversation}/members/{member.Id}");
        await WaitForLockWaiters(1);
        var send = memberHttp.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { body = "after removal", clientMessageId = Guid.NewGuid() });
        var receipt = memberHttp.PutAsJsonAsync($"/api/conversations/{conversation}/read-state",
            new { messageId });
        var typing = tab.InvokeAsync("SetTyping", conversation, true);
        await hold.CommitAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await removal).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await send).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await receipt).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => typing);

        async Task WaitForLockWaiters(int expected)
        {
            using var probeScope = topology.A.Services.CreateScope();
            var probe = probeScope.ServiceProvider.GetRequiredService<ChatDbContext>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var count = await probe.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                    .SingleAsync(deadline.Token);
                if (count >= expected) return;
                await Task.Delay(25, deadline.Token);
            }
        }
    }

    [Fact]
    public async Task FutureProtectedMessagesReceiptsPresenceAndTypingExcludeRemovedTabs()
    {
        using var ownerHttp = Client(topology.AUrl); using var memberHttp = Client(topology.BUrl);
        var owner = await Login(ownerHttp); var member = await Login(memberHttp);
        var created = await ownerHttp.PostAsJsonAsync("/api/conversations/group",
            new { name = "Protected publications", memberIds = new[] { member.Id } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await using var ownerHub = Hub(topology.AUrl, owner.Cookie);
        await using var tabA = Hub(topology.AUrl, member.Cookie);
        await using var tabB = Hub(topology.BUrl, member.Cookie);
        foreach (var hub in new[] { ownerHub, tabA, tabB })
        { await hub.StartAsync(); await hub.InvokeAsync("Subscribe", conversation); }
        var messages = new ConcurrentQueue<object>(); var receipts = new ConcurrentQueue<object>();
        var presence = new ConcurrentQueue<object>(); var typing = new ConcurrentQueue<object>();
        using var m1 = tabA.On<MessageResponse>(MessageBroadcaster.EventName, x => messages.Enqueue(x));
        using var m2 = tabB.On<MessageResponse>(MessageBroadcaster.EventName, x => messages.Enqueue(x));
        using var r1 = tabA.On<ReceiptAdvanced>(ReceiptBroadcaster.EventName, x => receipts.Enqueue(x));
        using var r2 = tabB.On<ReceiptAdvanced>(ReceiptBroadcaster.EventName, x => receipts.Enqueue(x));
        using var p1 = tabA.On<ConversationPresenceChanged>(ConversationPresence.EventName, x => presence.Enqueue(x));
        using var p2 = tabB.On<ConversationPresenceChanged>(ConversationPresence.EventName, x => presence.Enqueue(x));
        using var t1 = tabA.On<TypingChanged>(ConversationTyping.EventName, x => typing.Enqueue(x));
        using var t2 = tabB.On<TypingChanged>(ConversationTyping.EventName, x => typing.Enqueue(x));
        Assert.Equal(HttpStatusCode.NoContent,
            (await ownerHttp.DeleteAsync($"/api/conversations/{conversation}/members/{member.Id}")).StatusCode);
        await Probe(tabA); await Probe(tabB);
        messages.Clear(); receipts.Clear(); presence.Clear(); typing.Clear();
        var sent = await ownerHttp.PostAsJsonAsync($"/api/conversations/{conversation}/messages",
            new { body = "after revoke", clientMessageId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var messageId = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK,
            (await ownerHttp.PutAsJsonAsync($"/api/conversations/{conversation}/read-state", new { messageId })).StatusCode);
        await ownerHub.InvokeAsync("SetTyping", conversation, true);
        await using var anotherOwnerTab = Hub(topology.BUrl, owner.Cookie);
        await anotherOwnerTab.StartAsync();
        await Probe(tabA); await Probe(tabB);
        Assert.Empty(messages); Assert.Empty(receipts); Assert.Empty(presence); Assert.Empty(typing);
    }
}
