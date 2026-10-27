using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Chat.Api.Authorization;

public sealed class HttpContextCurrentUser(
    IHttpContextAccessor contextAccessor,
    IOptions<IdentityOptions> identityOptions) : ICurrentUser
{
    public Guid? UserId
    {
        get
        {
            var principal = contextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
                return null;

            var claim = principal.FindFirst(identityOptions.Value.ClaimsIdentity.UserIdClaimType);
            return Guid.TryParse(claim?.Value, out var id) && id != Guid.Empty ? id : null;
        }
    }
}
