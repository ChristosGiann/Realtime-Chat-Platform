using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chat.Api.Profiles;

public sealed record PublicProfileResponse(Guid UserId, string DisplayName, string? Bio, string? AvatarUrl);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateProfileRequest : IValidatableObject
{
    [Required, StringLength(100)]
    public string? DisplayName { get; init; }

    [StringLength(1000)]
    public string? Bio { get; init; }

    [StringLength(2048)]
    public string? AvatarUrl { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
            (!Uri.TryCreate(AvatarUrl.Trim(), UriKind.Absolute, out var uri) ||
             uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
             !string.IsNullOrEmpty(uri.UserInfo)))
        {
            yield return new ValidationResult(
                "Avatar URL must be an absolute HTTPS URL without embedded credentials.", [nameof(AvatarUrl)]);
        }
    }
}
