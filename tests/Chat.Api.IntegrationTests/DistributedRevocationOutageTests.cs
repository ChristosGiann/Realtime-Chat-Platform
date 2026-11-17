using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed class DistributedRevocationOutageTests(MultiInstanceTopology topology) : IClassFixture<MultiInstanceTopology>
{
    [Fact]
    public async Task RedisOutageBeforeBarrierReturns503AndKeepsPostgreSqlMembership()
    {
        using var owner = new HttpClient { BaseAddress = topology.AUrl };
        using var member = new HttpClient { BaseAddress = topology.BUrl };
        foreach (var client in new[] { owner, member })
        {
            var name = Guid.NewGuid().ToString("N"); var email = $"{name}@example.test";
            var registered = await client.PostAsJsonAsync("/api/auth/register",
                new { email, userName = name, password = "Test-password123!" });
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf"))
                .GetProperty("requestToken").GetString();
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
            Assert.Equal(HttpStatusCode.NoContent,
                (await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" })).StatusCode);
            csrf = (await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf"))
                .GetProperty("requestToken").GetString();
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
        }
        var memberId = (await member.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("id").GetGuid();
        var created = await owner.PostAsJsonAsync("/api/conversations/group",
            new { name = "Redis outage", memberIds = new[] { memberId } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var conversation = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await topology.Redis.StopAsync();
        var removed = await owner.DeleteAsync($"/api/conversations/{conversation}/members/{memberId}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, removed.StatusCode);
        using var scope = topology.A.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<ChatDbContext>().ConversationMembers
            .AnyAsync(x => x.ConversationId == conversation && x.UserId == memberId));
    }
}
