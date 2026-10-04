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
}

/// <summary>Offline speech synthesis.</summary>
public interface ITextToSpeech : IDisposable
{
    ComponentState State { get; }
    string? LastError { get; }
    /// <summary>Active voices, one per language.</summary>
    IReadOnlyList<VoiceInfo> Voices { get; }
    /// <summary>Every installed voice that can be selected.</summary>
    IReadOnlyList<VoiceInfo> AvailableVoices { get; }
    /// <summary>Speaking rate multiplier (1 = normal, &gt;1 faster).</summary>
    float Speed { get; set; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    /// <summary>Voice used for the given language (falls back to the configured default).</summary>
    VoiceInfo? GetVoice(string? language);
    /// <summary>Makes <paramref name="voiceId"/> the voice for <paramref name="language"/> (loads it if needed).</summary>
    Task SetVoiceAsync(string language, string voiceId, CancellationToken cancellationToken = default);
    Task<AudioClip> SynthesizeAsync(string text, string? language, CancellationToken cancellationToken = default);
}
