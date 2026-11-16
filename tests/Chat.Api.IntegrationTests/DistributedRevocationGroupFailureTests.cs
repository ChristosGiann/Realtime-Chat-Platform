using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Infrastructure.Redis;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using StackExchange.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class DistributedRevocationGroupFailureTests
{
    [Fact]
    public async Task RemoteGroupCleanupFailureAbortsSocketNacksAndRollsBack()
    {
        var topology = new MultiInstanceTopology { FailBGroupRemoval = true };
        await topology.InitializeAsync();
        try
        {
            using var owner = new HttpClient { BaseAddress = topology.AUrl };
            using var member = new HttpClient { BaseAddress = topology.BUrl };
            Guid memberId = Guid.Empty; string memberCookie = "";
            foreach (var client in new[] { owner, member })
            {
                var name = Guid.NewGuid().ToString("N"); var email = $"{name}@example.test";
                var registered = await client.PostAsJsonAsync("/api/auth/register",
                    new { email, userName = name, password = "Test-password123!" });
                Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
                if (client == member) memberId = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
                var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf"))
                    .GetProperty("requestToken").GetString();
                client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
                var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" });
                Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
                if (client == member)
                    memberCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Chat.Session="))
                        .Split(';')[0];
                csrf = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf"))
                    .GetProperty("requestToken").GetString();
                client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
            }
            var created = await owner.PostAsJsonAsync("/api/conversations/group",
                new { name = "Group failure", memberIds = new[] { memberId } });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            await using var socket = new HubConnectionBuilder()
                .WithUrl(new Uri(topology.BUrl, "/hubs/chat"), options => options.Headers.Add("Cookie", memberCookie))
                .Build();
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            socket.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            await socket.StartAsync(); await socket.InvokeAsync("Subscribe", conversation);
            var redis = await topology.A.Services.GetRequiredService<RedisConnection>().GetAsync();
            var channel = RedisChannel.Literal(topology.A.Services.GetRequiredService<RedisNamespace>().CoordinationPrefix
                + "revoke-command:" + topology.B.Services.GetRequiredService<DistributedConnectionRegistry>().Owner);
            var queue = await redis.GetSubscriber().SubscribeAsync(channel);
            var duplicated = 0;
            queue.OnMessage(async message =>
            {
                if (Interlocked.Exchange(ref duplicated, 1) == 0)
                    await redis.GetSubscriber().PublishAsync(channel, message.Message);
            });
            var removed = await owner.DeleteAsync($"/api/conversations/{conversation}/members/{memberId}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, removed.StatusCode);
            Assert.Equal(1, duplicated);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.UnsubscribeAsync();
            using var scope = topology.A.Services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
                .AnyAsync(x => x.ConversationId == conversation && x.UserId == memberId));
        }
        finally { await topology.DisposeAsync(); }
    }
}
