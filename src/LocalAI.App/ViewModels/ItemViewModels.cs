using CommunityToolkit.Mvvm.ComponentModel;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;

namespace LocalAI.App.ViewModels;

public sealed partial class MessageViewModel : ObservableObject
{
    public MessageViewModel(ChatRole role, string text, DateTimeOffset timestamp, InputSource source, string? language, string assistantName)
    {
        Role = role;
        _text = text;
        Timestamp = timestamp;
        Source = source;
        Language = language;
        Author = role == ChatRole.User ? "You" : role == ChatRole.Assistant ? assistantName : "System";
    }

    public static MessageViewModel From(StoredMessage m, string assistantName) =>
        new(m.Role, m.Content, m.CreatedAt, m.Source, m.Language, assistantName)
        {
            Interrupted = m.MetadataJson?.Contains("\"interrupted\":true", StringComparison.Ordinal) == true,
        };

    public ChatRole Role { get; }
    public string Author { get; }
    public DateTimeOffset Timestamp { get; }
    public InputSource Source { get; }
    public string? Language { get; }

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsSystem => Role == ChatRole.System;

    public string Meta => string.Join("  ·  ", new[]
    {
        Timestamp.ToString("HH:mm"),
        Source == InputSource.Voice ? "voice" : null,
        Language,
    }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty] private string _text;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _interrupted;
}

public sealed partial class ConversationItemViewModel(Conversation conversation) : ObservableObject
{
    public long Id { get; } = conversation.Id;
    [ObservableProperty] private string _title = conversation.Title;
    [ObservableProperty] private DateTimeOffset _updatedAt = conversation.UpdatedAt;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _editTitle = conversation.Title;
    [ObservableProperty] private string? _snippet;

    public string When => UpdatedAt.Date == DateTimeOffset.Now.Date ? UpdatedAt.ToString("HH:mm") : UpdatedAt.ToString("dd MMM yyyy");

    partial void OnUpdatedAtChanged(DateTimeOffset value) => OnPropertyChanged(nameof(When));
}

public sealed partial class PageTabViewModel(AppPage page, string title, bool isClosable) : ObservableObject
{
    public AppPage Page { get; } = page;
    public bool IsClosable { get; } = isClosable;
    [ObservableProperty] private string _title = title;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class MemoryItemViewModel(long id, string content, DateTimeOffset createdAt) : ObservableObject
{
    public long Id { get; } = id;
    public string Content { get; } = content;
    public string Created { get; } = createdAt.ToString("dd MMM yyyy HH:mm");
}
