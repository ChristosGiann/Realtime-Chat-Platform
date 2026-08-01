using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chat.Api.IntegrationTests;

public sealed class HealthEndpointTests
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(
        WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LiveHealthEndpoint_ReturnsOk()
    {
        using var response =
            await _client.GetAsync("/health/live");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);
    }
}