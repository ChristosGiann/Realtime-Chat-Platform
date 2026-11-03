namespace Chat.Api.Messages;

public sealed class Message
{
    public const int MaxBodyLength = 4000;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
