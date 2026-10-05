using LocalAI.Core.Speech;

namespace LocalAI.Core.Assistants;

/// <summary>
/// One assistant. Name, style, language model and voice are chosen at creation and never change; the voice style
/// (speed, pitch, ...) can be tuned later. The assistant always speaks and replies in its voice's language.
/// Conversations and memories belong to exactly one assistant.
/// </summary>
public sealed record AssistantProfile(
    long Id,
    string Name,
    string StylePrompt,
    string ModelId,
    string VoiceId,
    string Language,
    VoiceStyle VoiceStyle,
    DateTimeOffset CreatedAt);

/// <summary>How much an assistant has stored: what deleting it removes.</summary>
public sealed record AssistantDataSummary(int Conversations, int Messages, int Memories);

/// <summary>What the user chooses when creating an assistant.</summary>
public sealed record NewAssistant(string Name, string StylePrompt, string ModelId, string VoiceId, string Language);

public interface IAssistantStore
{
    Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default);
    /// <summary>Creates an assistant. The first one also adopts data created before assistants existed.</summary>
    Task<AssistantProfile> CreateAsync(NewAssistant assistant, VoiceStyle voiceStyle, CancellationToken ct = default);
    Task SetVoiceStyleAsync(long id, VoiceStyle voiceStyle, CancellationToken ct = default);
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
