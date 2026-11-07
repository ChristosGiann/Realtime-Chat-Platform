using System.Security.Claims;
using Chat.Api.Identity;
using Microsoft.AspNetCore.Identity;

namespace Chat.Api.Realtime;

public sealed class ChatSessionValidator(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users)
{
    public async Task<bool> IsValidAsync(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(users.GetUserId(principal), out var id) || id == Guid.Empty) return false;
        var user = await signIn.ValidateSecurityStampAsync(principal);
        return user is not null && await signIn.CanSignInAsync(user) && !await users.IsLockedOutAsync(user);
    }
}
