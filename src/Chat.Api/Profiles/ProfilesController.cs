using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Profiles;

[ApiController]
[Route("api/profiles")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProfilesController(
    ChatDbContext db,
    ICurrentUser currentUser,
    IAntiforgery antiforgery) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{userId:guid}")]
    [ProducesResponseType<PublicProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken cancellationToken)
    {
        // Project only approved public fields; never serialize an Identity entity.
        var profile = await db.Profiles.AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => new PublicProfileResponse(profile.UserId, profile.DisplayName, profile.Bio, profile.AvatarUrl))
            .SingleOrDefaultAsync(cancellationToken);
        return profile is null ? MissingProfile() : Ok(profile);
    }

    [Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
    [HttpPut("me")]
    [ProducesResponseType<PublicProfileResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Problem(statusCode: 400, title: "Invalid CSRF token.");
        }

        var userId = currentUser.UserId;
        if (userId is null)
            return Problem(statusCode: 401, title: "Authentication required.");

        // Ownership comes exclusively from the authenticated principal.
        var profile = await db.Profiles.SingleOrDefaultAsync(profile => profile.UserId == userId.Value, cancellationToken);
        if (profile is null)
            return MissingProfile();

        profile.DisplayName = request.DisplayName!.Trim();
        profile.Bio = OptionalText(request.Bio);
        profile.AvatarUrl = OptionalText(request.AvatarUrl);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new PublicProfileResponse(profile.UserId, profile.DisplayName, profile.Bio, profile.AvatarUrl));
    }

    private ObjectResult MissingProfile() => Problem(statusCode: 404, title: "Profile not found.");
    private static string? OptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
