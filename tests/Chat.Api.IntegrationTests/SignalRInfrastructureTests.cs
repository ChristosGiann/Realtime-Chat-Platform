using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Api.IntegrationTests;

public sealed partial class MessageCreationTests
{
    private static async Task<(Guid UserId, string Cookie)> HubLogin(HttpClient client)
    {
        var userName = Guid.NewGuid().ToString("N"); var email = $"{userName}@example.test";
        var register = await client.PostAsJsonAsync("/api/auth/register", new { email, userName, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var id = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Csrf(client);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Test-password123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-Chat.Session=")).Split(';')[0];
        await Csrf(client);
        return (id, cookie);
    }

    private HubConnection HubConnection(string? cookie, string? origin = "https://localhost", bool skipNegotiation = false,
        HttpTransportType transport = HttpTransportType.WebSockets, WebApplicationFactory<Program>? factory = null)
    {
        factory ??= fixture.Factory;
        var server = factory.Server;
        return new HubConnectionBuilder().WithUrl("https://localhost/hubs/chat", options =>
        {
            options.Transports = transport; options.SkipNegotiation = skipNegotiation;
            if (cookie is not null) options.Headers.Add("Cookie", cookie);
            if (origin is not null) options.Headers.Add("Origin", origin);
            options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            options.WebSocketFactory = async (context, cancellationToken) =>
            {
                var socket = server.CreateWebSocketClient();
                if (cookie is not null) socket.ConfigureRequest = request =>
                { request.Headers["Cookie"] = cookie; if (origin is not null) request.Headers["Origin"] = origin; };
                else if (origin is not null) socket.ConfigureRequest = request => request.Headers["Origin"] = origin;
                return await socket.ConnectAsync(context.Uri, cancellationToken);
            };
        }).Build();
    }

    [Theory]
    [InlineData(HttpTransportType.WebSockets)]
    [InlineData(HttpTransportType.LongPolling)]
    public async Task SignalRAuthenticatedCookieConnectsWithoutChangingRest(HttpTransportType transport)
    {
        using var client = Client(); var login = await HubLogin(client);
        await using var hub = HubConnection(login.Cookie, transport: transport);
        await hub.StartAsync(); Assert.Equal(HubConnectionState.Connected, hub.State);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => hub.InvokeAsync("PersistMessage", "not supported"));
        await hub.StopAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("__Host-Chat.Session=tampered")]
    public async Task SignalRAnonymousAndInvalidCookiesCannotNegotiateOrSkipNegotiation(string? cookie)
    {
        using var client = fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        var response = await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Null(response.Headers.Location);
        foreach (var skip in new[] { false, true })
        {
            await using var hub = HubConnection(cookie, skipNegotiation: skip);
            await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
            Assert.Equal(HubConnectionState.Disconnected, hub.State);
        }
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("stamp")]
    [InlineData("locked")]
    public async Task SignalRRechecksIdentityInsteadOfTrustingCachedCookie(string scenario)
    {
        using var client = Client(); var login = await HubLogin(client);
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(login.UserId.ToString()))!;
        if (scenario == "deleted") await Db(scope).Users.Where(x => x.Id == login.UserId).ExecuteDeleteAsync();
        else if (scenario == "stamp") Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        else { Assert.True((await users.SetLockoutEnabledAsync(user, true)).Succeeded); Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1))).Succeeded); }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null)).StatusCode);
        await using var hub = HubConnection(login.Cookie, skipNegotiation: true);
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task SignalRRejectsExpiredTicket()
    {
        var clock = new HubClock();
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.TimeProvider = clock)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var login = await HubLogin(client); clock.Advance();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null)).StatusCode);
        await using var hub = HubConnection(login.Cookie, factory: factory);
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task SignalRClosesConnectionAtTicketExpiration()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.ExpireTimeSpan = TimeSpan.FromSeconds(3))));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var login = await HubLogin(client);
        await using var hub = HubConnection(login.Cookie, factory: factory);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    public async Task SignalRRejectsUntrustedOriginsIncludingDirectWebSocket(string origin)
    {
        using var client = Client(); var login = await HubLogin(client); client.DefaultRequestHeaders.Add("Origin", origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null)).StatusCode);
        await using var hub = HubConnection(login.Cookie, origin, skipNegotiation: true);
        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task SignalRUsesConfiguredFrontendOriginAndCredentials()
    {
        using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(config,
                new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "https://frontend.example" })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var login = await HubLogin(client); client.DefaultRequestHeaders.Add("Origin", "https://frontend.example");
        var response = await client.PostAsync("/hubs/chat/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://frontend.example", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
        await using var hub = HubConnection(login.Cookie, "https://frontend.example", factory: factory);
        await hub.StartAsync(); await hub.StopAsync();
    }

    private sealed class HubClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now += TimeSpan.FromHours(9);
    }
}
