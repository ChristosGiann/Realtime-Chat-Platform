using System.Security.Claims;
using Chat.Api.Identity;
using Microsoft.AspNetCore.Identity;

namespace Chat.Api.Realtime;

public sealed class ChatSessionValidator(SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users)
{
    public Guid? GetUserId(ClaimsPrincipal? principal) => principal?.Identity?.IsAuthenticated == true
        && Guid.TryParse(users.GetUserId(principal), out var id) && id != Guid.Empty ? id : null;

    public async Task<bool> IsValidAsync(ClaimsPrincipal? principal)
    {
        if (GetUserId(principal) is null) return false;
        var user = await signIn.ValidateSecurityStampAsync(principal!);
        return user is not null && await signIn.CanSignInAsync(user) && !await users.IsLockedOutAsync(user);
    }
}
