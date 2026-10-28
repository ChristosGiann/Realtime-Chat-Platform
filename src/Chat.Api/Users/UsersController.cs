using System.Security.Cryptography;
using System.Text.Json;
using Chat.Api.Authorization;
using Chat.Api.Infrastructure.Persistence;
using Chat.Api.Profiles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chat.Api.Users;

[ApiController]
[Route("api/users")]
[Authorize(Policy = AuthorizationPolicies.AuthenticatedUser)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class UsersController(
    ChatDbContext db,
    ICurrentUser currentUser,
    IDataProtectionProvider dataProtection) : ControllerBase
{
    private readonly IDataProtector _cursorProtector = dataProtection.CreateProtector("UserSearch.Cursor.v1");
    private sealed record SearchCursor(string Query, Guid ViewerId, Guid LastUserId);

    [HttpGet]
    [ProducesResponseType<UserSearchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Search([FromQuery] UserSearchRequest request, CancellationToken cancellationToken)
    {
        var viewerId = currentUser.UserId;
        if (viewerId is null)
            return Problem(statusCode: 401, title: "Authentication required.");

        var query = request.Query?.Trim() ?? string.Empty;
        Guid? after = null;
        if (request.Cursor is not null)
        {
            try
            {
                var cursor = JsonSerializer.Deserialize<SearchCursor>(_cursorProtector.Unprotect(request.Cursor));
                if (cursor is null || cursor.Query != query || cursor.ViewerId != viewerId.Value || cursor.LastUserId == Guid.Empty)
                    return InvalidCursor();
                after = cursor.LastUserId;
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
            {
                return InvalidCursor();
            }
        }

        if (query.Length == 0)
            return Ok(new UserSearchResponse([], null));

        // Escape LIKE metacharacters so user input is always a literal substring.
        var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var matches = from profile in db.Profiles.AsNoTracking()
                      join user in db.Users.AsNoTracking() on profile.UserId equals user.Id
                      where profile.UserId != viewerId.Value &&
                          (EF.Functions.ILike(user.UserName!, pattern, "\\") ||
                           EF.Functions.ILike(profile.DisplayName, pattern, "\\"))
                      select profile;

        if (after is not null)
            matches = matches.Where(profile => profile.UserId.CompareTo(after.Value) > 0);

        // The immutable, unique key keeps paging stable when names change or previous rows disappear.
        var items = await matches.OrderBy(profile => profile.UserId)
            .Select(profile => new PublicProfileResponse(profile.UserId, profile.DisplayName, profile.Bio, profile.AvatarUrl))
            .Take(request.PageSize + 1)
            .ToListAsync(cancellationToken);
        string? nextCursor = null;
        if (items.Count > request.PageSize)
        {
            items.RemoveAt(items.Count - 1);
            nextCursor = _cursorProtector.Protect(JsonSerializer.Serialize(
                new SearchCursor(query, viewerId.Value, items[^1].UserId)));
        }
        return Ok(new UserSearchResponse(items, nextCursor));
    }

    private ObjectResult InvalidCursor() => Problem(statusCode: 400, title: "Invalid cursor for this search.");
}
