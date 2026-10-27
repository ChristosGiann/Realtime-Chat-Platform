using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Chat.Api.Authentication;

public static class SessionConfiguration
{
    public static IServiceCollection AddBrowserSessions(this IServiceCollection services, bool development)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
        }).AddIdentityCookies();
        services.AddAuthorization();
        services.AddSingleton<RejectedLoginVerifier>();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = development ? "Chat.Session" : "__Host-Chat.Session";
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => WriteProblem(context, 401, "Authentication required.");
            options.Events.OnRedirectToAccessDenied = context => WriteProblem(context, 403, "Access denied.");
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = development ? "Chat.Csrf" : "__Host-Chat.Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.Path = "/";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        return services;
    }

    private static Task WriteProblem(
        Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> context,
        int status, string title) =>
        Results.Problem(statusCode: status, title: title).ExecuteAsync(context.HttpContext);
}
