namespace LocalAI.Core.Speech;

public sealed record Transcription(
    string Text,
    string? Language,
    float LanguageProbability,
    TimeSpan AudioDuration,
    TimeSpan ProcessingTime);

/// <summary>Mono PCM audio, float samples in [-1, 1].</summary>
public sealed record AudioClip(float[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

public sealed record VoiceInfo(string Id, string Language, string DisplayName);

public enum ComponentState { NotInitialized, Initializing, Ready, Unavailable }

/// <summary>Offline speech recognition. Input is 16 kHz mono float PCM.</summary>
public interface ISpeechToText : IDisposable
{
    public const int SampleRate = 16000;

    ComponentState State { get; }
    string? Description { get; }
    string? LastError { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<Transcription> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default);
    /// <summary>Releases the model so its file can be removed. <see cref="InitializeAsync"/> loads it again.</summary>
    void Unload();
}

/// <summary>
/// How a voice is rendered. Speed, expressiveness and rhythm are Piper parameters; pitch is applied after synthesis.
/// </summary>
public sealed record VoiceStyle
{
    public static readonly VoiceStyle Default = new();

    /// <summary>Speaking rate multiplier (1 = normal, &gt;1 faster).</summary>
    public double Speed { get; init; } = 1.0;
    /// <summary>Pitch shift in semitones (0 = original voice).</summary>
    public double Pitch { get; init; }
    /// <summary>Intonation variation (Piper noise scale): lower is flatter.</summary>
    public double Expressiveness { get; init; } = 0.667;
    /// <summary>Syllable timing variation (Piper noise width): lower is more regular.</summary>
    public double Rhythm { get; init; } = 0.8;
}

/// <summary>Offline speech synthesis with one active voice (the active assistant's).</summary>
public interface ITextToSpeech : IDisposable
{
    ComponentState State { get; }
    string? LastError { get; }
    /// <summary>The active voice, or null when none is loaded.</summary>
    VoiceInfo? Voice { get; }
    /// <summary>Every installed voice.</summary>
    IReadOnlyList<VoiceInfo> AvailableVoices { get; }
    VoiceStyle Style { get; set; }
    /// <summary>Loads the configured voice (<c>TextToSpeech:Voice</c>).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>Makes <paramref name="voiceId"/> the active voice (loads it if needed).</summary>
    Task SetVoiceAsync(string voiceId, CancellationToken cancellationToken = default);
    /// <summary>Releases a voice so its files can be removed.</summary>
    void RemoveVoice(string voiceId);
    /// <summary>Speaks with the active voice and style.</summary>
    Task<AudioClip> SynthesizeAsync(string text, CancellationToken cancellationToken = default);
    /// <summary>Speaks with any installed voice (previews, tests).</summary>
    Task<AudioClip> SynthesizeAsync(string text, string voiceId, VoiceStyle style, CancellationToken cancellationToken = default);
}
