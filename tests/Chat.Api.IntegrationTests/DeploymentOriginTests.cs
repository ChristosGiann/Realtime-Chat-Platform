using Chat.Api.Realtime;
using Chat.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Chat.Api.IntegrationTests;

public sealed class DeploymentOriginTests
{
    private static ChatOrigins Origins(string publicOrigin) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:PublicOrigin"] = publicOrigin
        }).Build(),
        new TestEnvironment());

    [Theory]
    [InlineData("https://chat.example.test", true)]
    [InlineData("https://evil.example.test", false)]
    [InlineData("http://chat.example.test", false)]
    public void PublicOrigin_IsExact(string origin, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http"; // TLS terminates at a trusted external proxy.
        request.Host = new HostString("internal-api:8080");
        request.Headers.Origin = origin;
        Assert.Equal(expected, Origins("https://chat.example.test").Allows(request));
    }

    [Fact]
    public void DirectSameOrigin_RemainsAllowed()
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "https";
        request.Host = new HostString("chat.example.test");
        request.Headers.Origin = "https://chat.example.test";
        Assert.True(Origins("https://other.example.test").Allows(request));
    }

    [Theory]
    [InlineData("chat.example.test", "chat.example.test", "https", true)]
    [InlineData("other.example.test", "chat.example.test", "https", false)]
    [InlineData("chat.example.test", "other.example.test", "https", false)]
    [InlineData("chat.example.test", "chat.example.test", "http", false)]
    [InlineData("chat.example.test", "chat.example.test", "https,http", false)]
    public void RailwayEdgeScheme_RequiresExactPublicHostAndSingleHttpsHeader(
        string requestHost, string forwardedHost, string forwardedProto, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Scheme = "http";
        request.Host = new HostString(requestHost);
        request.Headers["X-Forwarded-Host"] = forwardedHost;
        request.Headers["X-Forwarded-Proto"] = forwardedProto;
        Assert.Equal(expected, RailwayEdgeHttps.Matches(request, new Uri("https://chat.example.test")));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Chat.Api";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
