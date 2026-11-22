namespace Chat.Api.AiSummaries;

public sealed record SummaryPromptMessage(int Order, string Sender, string Text);
public sealed record SummaryContent(string Overview, IReadOnlyList<string> KeyPoints,
    IReadOnlyList<string> ActionItems, IReadOnlyList<string> UnresolvedQuestions);
public sealed record UnreadSummaryResponse(Guid? FromReadMessageId, Guid ThroughMessageId, DateTime ThroughCreatedAt,
    int UnreadMessagesSummarized, string Overview, IReadOnlyList<string> KeyPoints,
    IReadOnlyList<string> ActionItems, IReadOnlyList<string> UnresolvedQuestions);

public interface IUnreadSummaryGenerator
{
    Task<SummaryContent> GenerateAsync(IReadOnlyList<SummaryPromptMessage> messages, CancellationToken cancellationToken);
    Task<SummaryContent> SynthesizeAsync(IReadOnlyList<SummaryContent> chunks, CancellationToken cancellationToken);
}

public sealed class UnreadSummaryOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3:4b";
    public int TimeoutSeconds { get; set; } = 60;
    public int ChunkSize { get; set; } = 50;
    public int MaxMessages { get; set; } = 200;
    public bool IsValid() => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(Model)
        && TimeoutSeconds is >= 1 and <= 300 && ChunkSize is >= 1 and <= 100
        && MaxMessages is >= 1 and <= 500 && ChunkSize <= MaxMessages;
}

public static class SummaryContentValidation
{
    public static bool IsValid(SummaryContent? value) => value is not null
        && !string.IsNullOrWhiteSpace(value.Overview) && value.Overview.Length <= 1200
        && IsValidList(value.KeyPoints) && IsValidList(value.ActionItems) && IsValidList(value.UnresolvedQuestions);

    private static bool IsValidList(IReadOnlyList<string>? values) => values is not null && values.Count <= 8
        && values.All(item => !string.IsNullOrWhiteSpace(item) && item.Length <= 400);
}
