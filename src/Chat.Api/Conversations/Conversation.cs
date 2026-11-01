namespace Chat.Api.Conversations;

public enum ConversationType { Direct = 1, Group = 2 }
public enum ConversationMemberRole { Member = 1, Owner = 2 }

public sealed class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ConversationType Type { get; set; }
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
    // Direct pairs use PostgreSQL UUID ordering; both are null for groups.
    public Guid? DirectUserLowId { get; set; }
    public Guid? DirectUserHighId { get; set; }
    public ICollection<ConversationMember> Members { get; set; } = new List<ConversationMember>();
}

public sealed class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public ConversationMemberRole Role { get; set; } = ConversationMemberRole.Member;
    public DateTime JoinedAt { get; set; }
    public Conversation Conversation { get; set; } = null!;
}
