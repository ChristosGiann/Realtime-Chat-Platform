namespace Chat.Api.Realtime;

public sealed class ChatOrigins(IConfiguration configuration, IHostEnvironment environment)
{
    public string[] AllowedOrigins { get; } = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
        ?? (environment.IsDevelopment() ? ["http://localhost:5173"] : []);
    public bool Allows(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origins)) return true; // Native clients have no browser Origin.
        if (origins.Count != 1) return false;
        var origin = origins[0];
        return string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
            || AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class ChatConnectionSecurity(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ChatOrigins origins, ChatSessionValidator sessions)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!origins.Allows(context.Request))
        {
            await Results.Problem(statusCode: 403, title: "Origin not permitted.").ExecuteAsync(context); return;
        }
        if (!await sessions.IsValidAsync(context.User))
        {
            await Results.Problem(statusCode: 401, title: "Authentication required.").ExecuteAsync(context); return;
        }
        await next(context);
    }
}
