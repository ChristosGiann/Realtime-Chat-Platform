using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chat.Api.Conversations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateDirectConversationRequest : IValidatableObject
{
    [Required]
    public Guid? TargetUserId { get; init; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TargetUserId == Guid.Empty)
            yield return new ValidationResult("Target user ID must not be empty.", [nameof(TargetUserId)]);
    }
}

public sealed record DirectConversationResponse(Guid Id, string Type, DateTime CreatedAt, IReadOnlyList<Guid> MemberIds);
