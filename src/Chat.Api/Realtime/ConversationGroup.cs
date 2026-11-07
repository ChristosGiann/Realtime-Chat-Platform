namespace Chat.Api.Realtime;

public static class ConversationGroup
{
    public static string Name(Guid conversationId) => $"conversation:{conversationId:N}";
}
