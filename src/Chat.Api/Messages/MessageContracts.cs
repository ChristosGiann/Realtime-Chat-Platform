using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chat.Api.Messages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateMessageRequest : IValidatableObject
{
    [Required, StringLength(Message.MaxBodyLength)]
    public string? Body { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Body?.Contains('\0') == true)
            yield return new ValidationResult("Message body must not contain NUL characters.", [nameof(Body)]);
    }
}

public sealed record MessageResponse(Guid Id, Guid ConversationId, Guid SenderId, string Body, DateTime CreatedAt);
