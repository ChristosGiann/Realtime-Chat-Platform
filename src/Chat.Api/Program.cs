using Chat.Api.Authorization;
using Chat.Api.Conversations;
using Chat.Api.Realtime;
using Microsoft.AspNetCore.Identity;
using Chat.Api.Authentication;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddSignalR(options => options.EnableDetailedErrors = false);
builder.Services.AddScoped<ChatSessionValidator>();

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

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

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

app.MapGet("/api/version", () =>
{
    return Results.Ok(new
    {
        name = "Realtime Chat API",
        version = "0.1.0",
        environment = app.Environment.EnvironmentName
    });
}).AllowAnonymous();

app.Run();

// Required by WebApplicationFactory integration tests.
public partial class Program
{
}
