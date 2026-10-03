using LocalAI.Core.Llm;

namespace LocalAI.Core.Conversations;

public enum InputSource { Text, Voice }

public sealed record Conversation(long Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record StoredMessage
{
    public long Id { get; init; }
    public long ConversationId { get; init; }
    public ChatRole Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Language { get; init; }
    public string? Model { get; init; }
    public InputSource Source { get; init; }
    /// <summary>Free-form JSON (generation stats, interruption flag, ...).</summary>
    public string? MetadataJson { get; init; }
}

public sealed record ConversationSearchHit(Conversation Conversation, string Snippet);

public interface IConversationStore
{
    Task<Conversation> CreateAsync(string title, CancellationToken ct = default);
    Task<Conversation?> GetAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default);
    Task RenameAsync(long id, string title, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<StoredMessage> AddMessageAsync(StoredMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(long conversationId, CancellationToken ct = default);
    /// <summary>Most recent messages (chronological order).</summary>
    Task<IReadOnlyList<StoredMessage>> GetRecentMessagesAsync(long conversationId, int limit, CancellationToken ct = default);
    /// <summary>Case-insensitive search over titles and message content.</summary>
    Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, CancellationToken ct = default);
}
