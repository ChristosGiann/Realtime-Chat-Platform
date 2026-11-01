using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chat.Api.Conversations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateGroupConversationRequest : IValidatableObject
{
    [Required, StringLength(100)]
    public string? Name { get; init; }

    [Required]
    public Guid[]? MemberIds { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MemberIds is null) yield break;
        if (MemberIds.Contains(Guid.Empty))
            yield return new ValidationResult("Member IDs must not be empty.", [nameof(MemberIds)]);
        if (MemberIds.Distinct().Count() != MemberIds.Length)
            yield return new ValidationResult("Duplicate member IDs are not allowed.", [nameof(MemberIds)]);
    }
}

public sealed record GroupMemberResponse(Guid UserId, string Role, DateTime JoinedAt);
public sealed record GroupConversationResponse(Guid Id, string Type, string Name, DateTime CreatedAt,
    IReadOnlyList<GroupMemberResponse> Members);
