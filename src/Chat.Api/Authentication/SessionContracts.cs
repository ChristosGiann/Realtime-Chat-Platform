using System.ComponentModel.DataAnnotations;

namespace Chat.Api.Authentication;

public sealed class LoginRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string? Email { get; init; }

    [Required, StringLength(128)]
    public string? Password { get; init; }
}

public sealed record CurrentUserResponse(Guid Id, string UserName, string Email,
    string? DisplayName, string? Bio, string? AvatarUrl);
