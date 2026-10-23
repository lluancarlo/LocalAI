using Avalonia.Media;
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
            SelectedText = m.SelectedText,
            Interrupted = m.MetadataJson?.Contains("\"interrupted\":true", StringComparison.Ordinal) == true,
        };

    public ChatRole Role { get; }
    public string Author { get; }
    public DateTimeOffset Timestamp { get; }
    public InputSource Source { get; }
    public string? Language { get; }
    /// <summary>Text the user selected in another application and sent with this message (Read selection).</summary>
    public string? SelectedText { get; init; }
    public bool HasSelectedText => !string.IsNullOrEmpty(SelectedText);

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
    /// <summary>The assistant recalls this conversation: its note is among the ones the memory (RAG) searches.</summary>
    [ObservableProperty] private bool _inMemory;
    [ObservableProperty] private string _memoryTip = "";

    public string When => UpdatedAt.Date == DateTimeOffset.Now.Date ? UpdatedAt.ToString("HH:mm") : UpdatedAt.ToString("dd MMM yyyy");

    partial void OnUpdatedAtChanged(DateTimeOffset value) => OnPropertyChanged(nameof(When));
}

/// <summary>An assistant's tab at the top of the window.</summary>
public sealed partial class AssistantTabViewModel(long id, string name, string? hotkey) : ObservableObject
{
    public long Id { get; } = id;
    public string Name { get; } = name;
    public string Tip => hotkey == null ? $"Chat with {Name}" : $"Chat with {Name} · {hotkey} starts a live conversation";
    /// <summary>The assistant chats are shown for (its tab is underlined while the chat is on screen).</summary>
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSelected;
    /// <summary>The assistant is listening in live mode ("live" tag).</summary>
    [ObservableProperty] private bool _isLive;

    /// <summary>The loaded language model is this assistant's (green dot); assistants with the same model share it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush), nameof(StatusText))]
    private bool _isOnline;

    /// <summary>Its language model or its voice is loading (yellow dot).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush), nameof(StatusText))]
    private bool _isLoading;

    /// <summary>Its AI is turning off: its memory work runs first (yellow dot).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush), nameof(StatusText))]
    private bool _isSavingMemory;

    private static readonly IBrush Online = new SolidColorBrush(Color.Parse("#9ECE6A"));
    private static readonly IBrush Loading = new SolidColorBrush(Color.Parse(StatusColors.Background));
    private static readonly IBrush Offline = new SolidColorBrush(Color.Parse("#F7768E"));

    public IBrush StatusBrush => IsLoading || IsSavingMemory ? Loading : IsOnline ? Online : Offline;
    public string StatusText => IsSavingMemory ? "Saving memory before turning off…" : IsLoading ? "Loading…" : IsOnline ? "AI on" : "AI off";
}

/// <summary>A conversation note on the Memory page.</summary>
public sealed class MemoryItemViewModel(string path, string title, string body)
{
    /// <summary>Absolute path of the note file.</summary>
    public string Path { get; } = path;
    public string Title { get; } = title;
    public string Body { get; } = body;
}
