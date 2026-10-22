using LocalAI.Core.Desktop;
using LocalAI.Core.Speech;

namespace LocalAI.Core.Assistants;

/// <summary>
/// One assistant. Name, style, language model, voice and voice style (speed, pitch, ...) are chosen at creation and
/// never change. The assistant replies in the language the user writes or speaks, always with its own voice.
/// Conversations and memories belong to exactly one assistant. <see cref="Hotkey"/> is the optional global shortcut
/// that turns a live voice conversation with the assistant on and off.
/// </summary>
public sealed record AssistantProfile(
    long Id,
    string Name,
    string StylePrompt,
    string ModelId,
    string VoiceId,
    string Language,
    VoiceStyle VoiceStyle,
    DateTimeOffset CreatedAt,
    HotkeyGesture? Hotkey = null);

/// <summary>How much an assistant has stored: what deleting it removes.</summary>
/// <param name="Memories">Memory notes (the profile and conversation notes).</param>
public sealed record AssistantDataSummary(int Conversations, int Messages, int Memories);

/// <summary>What the user chooses when creating an assistant. <paramref name="VoiceStyle"/> null = the voice as it is.</summary>
public sealed record NewAssistant(string Name, string StylePrompt, string ModelId, string VoiceId, string Language,
    VoiceStyle? VoiceStyle = null);

public interface IAssistantStore
{
    Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default);
    /// <summary>Creates an assistant. The first one also adopts data created before assistants existed.</summary>
    Task<AssistantProfile> CreateAsync(NewAssistant assistant, VoiceStyle voiceStyle, CancellationToken ct = default);
    Task SetHotkeyAsync(long id, HotkeyGesture? hotkey, CancellationToken ct = default);
    /// <summary>Counts the conversations, messages and memories that belong to the assistant.</summary>
    Task<AssistantDataSummary> GetDataSummaryAsync(long id, CancellationToken ct = default);
    /// <summary>Deletes the assistant with all its conversations and memories.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>The active assistant. Conversation and memory stores read and write only its data.</summary>
public sealed class AssistantContext
{
    private AssistantProfile? _current;

    public AssistantProfile? Current => _current;

    public long CurrentId => _current?.Id ?? throw new InvalidOperationException("No assistant has been created yet.");

    public event EventHandler? Changed;

    public void Set(AssistantProfile? profile)
    {
        _current = profile;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Update(AssistantProfile profile) => _current = profile;
}

public sealed class AssistantValidationException(string message) : Exception(message);
