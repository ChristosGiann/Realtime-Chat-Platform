using Chat.Api.Infrastructure;
using Chat.Api.AiSummaries;
using Chat.Api.Infrastructure.Redis;
using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.Identity;
using Chat.Api.Authentication;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

var portValue = builder.Configuration["PORT"];
if (portValue is not null)
{
    if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        throw new InvalidOperationException("PORT must be a valid TCP port.");
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var publicOrigin = builder.Configuration["Deployment:PublicOrigin"];
Uri? publicUri = null;
if (publicOrigin is not null && (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out publicUri)
    || publicUri.Scheme != Uri.UriSchemeHttps || publicUri.AbsolutePath != "/"
    || !string.IsNullOrEmpty(publicUri.Query) || !string.IsNullOrEmpty(publicUri.Fragment)))
    throw new InvalidOperationException("Deployment:PublicOrigin must be an HTTPS origin without a path.");

var trustedProxies = builder.Configuration.GetSection("Deployment:TrustedProxies").Get<string[]>() ?? [];
var railwayEdgeMode = builder.Configuration.GetValue<bool>("Deployment:RailwayEdgeMode");
if (railwayEdgeMode && (publicUri is null || trustedProxies.Length > 0))
    throw new InvalidOperationException("Railway edge mode requires a public HTTPS origin and cannot be combined with TrustedProxies.");
if (trustedProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        foreach (var proxy in trustedProxies)
        {
            if (!IPAddress.TryParse(proxy, out var address))
                throw new InvalidOperationException("Deployment:TrustedProxies must contain IP addresses.");
            options.KnownProxies.Add(address);
        }
        if (publicUri is not null) options.AllowedHosts.Add(publicUri.Host);
    });
}

if (builder.Configuration.GetValue<bool>("Deployment:RequirePersistentKeys"))
{
    var keyPath = builder.Configuration["SharedDataProtection:KeyRingPath"];
    var appName = builder.Configuration["SharedDataProtection:ApplicationName"];
    if (string.IsNullOrWhiteSpace(keyPath) || !Path.IsPathFullyQualified(keyPath)
        || string.IsNullOrWhiteSpace(appName) || !Directory.Exists(keyPath))
        throw new InvalidOperationException("A mounted SharedDataProtection key ring and ApplicationName are required.");
}

builder.Services.AddRedisInfrastructure();
builder.Services.AddSharedDataProtection();
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.Configure<UnreadSummaryOptions>(builder.Configuration.GetSection("UnreadSummaries"));
builder.Services.AddHttpClient<IUnreadSummaryGenerator, OllamaUnreadSummaryGenerator>();
var signalR = builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});
var redisSettings = builder.Configuration.GetSection("Redis").Get<RedisOptions>();
if (redisSettings?.BackplaneEnabled == true)
{
    if (!redisSettings.IsValid()) throw new InvalidOperationException("Redis backplane configuration is invalid.");
    signalR.AddStackExchangeRedis(options =>
    {
        options.Configuration = StackExchange.Redis.ConfigurationOptions.Parse(redisSettings.ConnectionString);
        options.Configuration.AbortOnConnectFail = false;
        options.Configuration.ConnectTimeout = redisSettings.OperationTimeoutMilliseconds;
        options.Configuration.AsyncTimeout = redisSettings.OperationTimeoutMilliseconds;
        options.Configuration.BacklogPolicy = StackExchange.Redis.BacklogPolicy.FailFast;
        options.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal(
            $"{redisSettings.ApplicationName}:{redisSettings.Deployment}:{redisSettings.ProtocolVersion}:{redisSettings.RunId}:backplane:");
    });
}
builder.Services.AddScoped<ChatSessionValidator>();
builder.Services.AddSingleton<IMessageBroadcaster, MessageBroadcaster>();
builder.Services.AddSingleton<IReceiptBroadcaster, ReceiptBroadcaster>();
builder.Services.AddSingleton<MembershipNotifications>();
builder.Services.AddSingleton<ConversationConnections>();
builder.Services.AddSingleton<DistributedConnectionRegistry>();
builder.Services.AddHostedService(services => services.GetRequiredService<DistributedConnectionRegistry>());
builder.Services.AddSingleton<ConversationPresence>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ConversationTyping>();
builder.Services.AddSingleton<DistributedTyping>();
builder.Services.AddSingleton<RevocationBarrier>();
builder.Services.AddHostedService(services => services.GetRequiredService<RevocationBarrier>());
builder.Services.AddHostedService(services => services.GetRequiredService<ConversationTyping>());

builder.Services.AddDbContext<ChatDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>()
        .GetConnectionString("ChatDatabase")
        ?? throw new InvalidOperationException("The ChatDatabase connection string is missing.");
    options.UseNpgsql(connectionString);
});

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<ChatDbContext>()
    .AddSignInManager();

builder.Services.AddBrowserSessions(builder.Environment.IsDevelopment());
builder.Services.AddAuthorizationBaseline();
builder.Services.AddScoped<ConversationAuthorization>();

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<ChatDbContext>(
        name: "postgresql",
        tags: ["ready"]);

builder.Services.AddSingleton<ChatOrigins>();
builder.Services.AddCors();
builder.Services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<ChatOrigins>((options, origins) =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(origins.AllowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod().AllowCredentials();
    });
});

var app = builder.Build();

if (trustedProxies.Length > 0) app.UseForwardedHeaders();
if (railwayEdgeMode)
{
    app.Use(async (context, next) =>
    {
        // Railway's public edge overwrites these headers, but private services bypass it.
        // Trust this mode only in an isolated single-service Railway environment.
        if (RailwayEdgeHttps.Matches(context.Request, publicUri!)) context.Request.Scheme = "https";
        await next(context);
    });
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseWhen(context => context.Request.Path.StartsWithSegments(ChatHub.Endpoint),
    branch => branch.UseMiddleware<ChatConnectionSecurity>());
app.MapControllers();
app.MapHub<ChatHub>(ChatHub.Endpoint, options =>
{
    options.CloseOnAuthenticationExpiration = true;
    options.AllowStatefulReconnects = false;
});

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = _ => false
    }).AllowAnonymous();

app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = healthCheck =>
            healthCheck.Tags.Contains("ready")
    }).AllowAnonymous();

// Connectivity only. Keep durable readiness independent from Redis.
app.MapHealthChecks("/health/realtime", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("realtime"),
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable
    }
}).AllowAnonymous();

app.MapGet("/api/version", () =>
{
    return Results.Ok(new
    {
        name = "Realtime Chat API",
        version = "0.1.0",
        environment = app.Environment.EnvironmentName
    });
}).AllowAnonymous();

if (app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
{
    app.MapFallback(async context =>
    {
        if ((!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            || context.Request.Path.StartsWithSegments("/api")
            || context.Request.Path.StartsWithSegments("/hubs")
            || context.Request.Path.StartsWithSegments("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(app.Environment.WebRootFileProvider.GetFileInfo("index.html"));
    }).AllowAnonymous();
}

app.Run();

// Required by WebApplicationFactory integration tests.
public partial class Program
{
}
