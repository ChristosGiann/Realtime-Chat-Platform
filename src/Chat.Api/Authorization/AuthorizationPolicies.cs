using Microsoft.AspNetCore.Authorization;

namespace Chat.Api.Authorization;

public static class AuthorizationPolicies
{
    public const string AuthenticatedUser = "AuthenticatedUser";

    public static IServiceCollection AddAuthorizationBaseline(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        services.AddAuthorization(options =>
        {
            var authenticated = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(AuthenticatedUser, authenticated);
            options.DefaultPolicy = authenticated;
            options.FallbackPolicy = authenticated;
        });
        return services;
    }
}
