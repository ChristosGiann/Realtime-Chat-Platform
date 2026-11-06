using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Chat.Api.Messages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateMessageRequest : IValidatableObject
{
    [Required]
    public Guid? ClientMessageId { get; init; }
    [Required, StringLength(Message.MaxBodyLength)]
    public string? Body { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ClientMessageId == Guid.Empty)
            yield return new ValidationResult("Client message identifier must not be empty.", [nameof(ClientMessageId)]);
        if (Body?.Contains('\0') == true)
            yield return new ValidationResult("Message body must not contain NUL characters.", [nameof(Body)]);
    }
}

public sealed record MessageResponse(Guid Id, Guid ClientMessageId, Guid ConversationId, Guid SenderId, string Body, DateTime CreatedAt);
