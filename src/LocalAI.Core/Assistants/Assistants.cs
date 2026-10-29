using LocalAI.Core.Desktop;
using LocalAI.Core.Speech;

namespace LocalAI.Core.Assistants;

/// <summary>
/// One assistant. Name, style, language model, voice and voice style (speed, pitch, ...) are chosen at creation and
/// never change. The assistant replies in the language the user writes or speaks, always with its own voice.
/// Conversations and memories belong to exactly one assistant. <see cref="Hotkey"/> is the optional global shortcut
/// that turns a live voice conversation with the assistant on and off. <see cref="Avatar"/> names its picture
/// (<see cref="AssistantAvatars"/>; empty until one is chosen). <see cref="Behavior"/> (how it replies) can change.
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
    HotkeyGesture? Hotkey = null,
    string Avatar = "",
    AssistantBehavior? Behavior = null)
{
    /// <summary>How the assistant replies; the defaults until the user changes them.</summary>
    public AssistantBehavior Behavior { get; init; } = Behavior ?? AssistantBehavior.Default;

    /// <summary>Speech recognition (Whisper) loads with its Brain (AI): the user's last choice, on by default.</summary>
    public bool Hearing { get; init; } = true;

    /// <summary>Its voice (Kokoro) loads with its Brain (AI): the user's last choice, on by default.</summary>
    public bool Speaker { get; init; } = true;
}

/// <summary>How varied the replies are: the sampling temperature.</summary>
public enum Creativity { Precise, Balanced, Creative }

/// <summary>How long the replies are: an instruction to the model and a token limit.</summary>
public enum ReplyLength { Short, Normal, Detailed }

/// <summary>
/// How an assistant replies, changeable at any time (Settings › Assistants): <see cref="Creativity"/>,
/// <see cref="ReplyLength"/> and <see cref="Thinking"/> (models that can reason think before they answer: better
/// answers, slower to start; never in Live mode).
/// </summary>
public sealed record AssistantBehavior
{
    public static readonly AssistantBehavior Default = new();

    /// <summary>Room for the thinking of a model that reasons, besides the reply.</summary>
    public const int ThinkingTokens = 4096;

    public Creativity Creativity { get; init; } = Creativity.Balanced;
    public ReplyLength ReplyLength { get; init; } = ReplyLength.Normal;
    public bool Thinking { get; init; }
    /// <summary>Tokens of context the model is started with; 0 = the model's default. Changing it restarts the model.</summary>
    public int ContextSize { get; init; }
    /// <summary>The 8-bit KV cache: about half the memory for the context. Changing it restarts the model.</summary>
    public bool CompactCache { get; init; }

    public float Temperature => Creativity switch
    {
        Creativity.Precise => 0.3f,
        Creativity.Creative => 1.0f,
        _ => 0.7f,
    };

    /// <summary>The reply's token limit (without the thinking).</summary>
    public int ReplyTokens => ReplyLength switch
    {
        ReplyLength.Short => 300,
        ReplyLength.Detailed => 2048,
        _ => 1024,
    };
}

/// <summary>The built-in pictures of the assistants (for now chosen from the gender of the voice).</summary>
public static class AssistantAvatars
{
    public const string Female = "bot-female";
    public const string Male = "bot-male";
    /// <summary>When the voice's gender is not known.</summary>
    public const string Default = "mascot";

    /// <summary>The picture for a voice of this gender ("female", "male" or unknown).</summary>
    public static string ForGender(string? gender) => gender?.ToLowerInvariant() switch
    {
        "female" => Female,
        "male" => Male,
        _ => Default,
    };
}

/// <summary>How much an assistant has stored: what deleting it removes.</summary>
/// <param name="Memories">Memory notes (the profile and conversation notes).</param>
public sealed record AssistantDataSummary(int Conversations, int Messages, int Memories);

/// <summary>What the user chooses when creating an assistant. <paramref name="VoiceStyle"/> null = the voice as it is.</summary>
public sealed record NewAssistant(string Name, string StylePrompt, string ModelId, string VoiceId, string Language,
    VoiceStyle? VoiceStyle = null, string Avatar = "", AssistantBehavior? Behavior = null);

public interface IAssistantStore
{
    Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default);
    /// <summary>Creates an assistant. The first one also adopts data created before assistants existed.</summary>
    Task<AssistantProfile> CreateAsync(NewAssistant assistant, VoiceStyle voiceStyle, CancellationToken ct = default);
    Task SetHotkeyAsync(long id, HotkeyGesture? hotkey, CancellationToken ct = default);
    Task SetAvatarAsync(long id, string avatar, CancellationToken ct = default);
    Task SetBehaviorAsync(long id, AssistantBehavior behavior, CancellationToken ct = default);
    /// <summary>Remembers whether the assistant's hearing and speaker are on (they load with its Brain).</summary>
    Task SetSensesAsync(long id, bool hearing, bool speaker, CancellationToken ct = default);
    /// <summary>Counts the conversations, messages and memories that belong to the assistant.</summary>
    Task<AssistantDataSummary> GetDataSummaryAsync(long id, CancellationToken ct = default);
    /// <summary>Deletes the assistant with all its conversations and memories.</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);
}

/// <summary>
/// What runs only while an AI is on, besides the language model and the voice: speech recognition and the embedding
/// model. Every assistant shares them. Without the language model they have nothing to do, so they load after it and
/// unload with it.
/// </summary>
public interface IAiCompanions
{
    /// <summary>
    /// Loads the embedding model, and speech recognition when <paramref name="hearing"/> (does nothing for those already
    /// loaded; speech recognition unloads when not hearing). A failure in one is reported, never thrown.
    /// </summary>
    Task StartAsync(bool hearing, CancellationToken ct = default);
    /// <summary>Loads or unloads speech recognition alone, while the AI is on.</summary>
    Task SetHearingAsync(bool on, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
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
