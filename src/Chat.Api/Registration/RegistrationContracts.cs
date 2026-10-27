using System.ComponentModel.DataAnnotations;

namespace Chat.Api.Registration;

public sealed class RegistrationRequest
{
    [Required, EmailAddress, StringLength(256)]
    public string? Email { get; init; }

    [Required, StringLength(100)]
    public string? UserName { get; init; }

    [Required, StringLength(128, MinimumLength = 6)]
    public string? Password { get; init; }

    [StringLength(100)]
    public string? DisplayName { get; init; }
}

public sealed record RegistrationResponse(Guid Id, string UserName, string Email, string DisplayName);
