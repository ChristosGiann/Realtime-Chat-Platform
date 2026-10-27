using Chat.Api.Identity;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Authentication;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SessionController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    ChatDbContext db,
    IAntiforgery antiforgery,
    RejectedLoginVerifier rejectedLogin) : ControllerBase
{
    [HttpGet("csrf")]
    public IActionResult Csrf() =>
        Ok(new { requestToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        if (!await ValidCsrfAsync())
            return Problem(statusCode: 400, title: "Invalid CSRF token.");

        var user = await users.FindByEmailAsync(request.Email!.Trim());
        if (user is null)
        {
            rejectedLogin.Verify(request.Password!);
            return InvalidCredentials();
        }

        var result = await signIn.PasswordSignInAsync(user, request.Password!,
            isPersistent: false, lockoutOnFailure: true);
        // Do not disclose unknown users, wrong passwords, lockout, or account restrictions.
        if (!result.Succeeded)
        {
            if (result.IsLockedOut || result.IsNotAllowed)
                rejectedLogin.Verify(request.Password!);
            return InvalidCredentials();
        }

        return NoContent();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(User);
        if (user is null)
            return Problem(statusCode: 401, title: "Authentication required.");

        var profile = await db.Profiles.AsNoTracking()
            .SingleOrDefaultAsync(profile => profile.UserId == user.Id, cancellationToken);
        return Ok(new CurrentUserResponse(user.Id, user.UserName!, user.Email!,
            profile?.DisplayName, profile?.Bio, profile?.AvatarUrl));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (!await ValidCsrfAsync())
            return Problem(statusCode: 400, title: "Invalid CSRF token.");

        await signIn.SignOutAsync();
        return NoContent();
    }

    private ObjectResult InvalidCredentials() =>
        Problem(statusCode: 401, title: "Invalid email or password.");

    private async Task<bool> ValidCsrfAsync()
    {
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
