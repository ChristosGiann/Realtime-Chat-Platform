using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddDbContext<ChatDbContext>((services, options) =>
{
    var connectionString = services.GetRequiredService<IConfiguration>()
        .GetConnectionString("ChatDatabase")
        ?? throw new InvalidOperationException("The ChatDatabase connection string is missing.");
    options.UseNpgsql(connectionString);
});

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<ChatDbContext>();

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<ChatDbContext>(
        name: "postgresql",
        tags: ["ready"]);

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("Frontend");
app.MapControllers();

app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = _ => false
    });

app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = healthCheck =>
            healthCheck.Tags.Contains("ready")
    });

app.MapGet("/api/version", () =>
{
    return Results.Ok(new
    {
        name = "Realtime Chat API",
        version = "0.1.0",
        environment = app.Environment.EnvironmentName
    });
});

app.Run();

// Required by WebApplicationFactory integration tests.
public partial class Program
{
}
