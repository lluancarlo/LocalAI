using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LocalAI.Speech;

/// <summary>
/// Offline speech recognition with whisper.cpp via Whisper.net (CUDA runtime first, CPU fallback).
/// The spoken language is auto-detected, optionally restricted to the configured languages (pt/it/en by default)
/// which avoids common confusions such as Portuguese → Galician/Spanish.
/// </summary>
public sealed class WhisperSpeechToText : ISpeechToText
{
    private readonly SpeechToTextOptions _options;
    private readonly LocalAiPaths _paths;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<WhisperSpeechToText> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public WhisperSpeechToText(IOptions<LocalAiOptions> options, LocalAiPaths paths, ModelCatalog catalog, ILogger<WhisperSpeechToText> logger)
    {
        _options = options.Value.SpeechToText;
        _paths = paths;
        _catalog = catalog;
        _logger = logger;
    }

    public ComponentState State { get; private set; } = ComponentState.NotInitialized;
    public string? Description { get; private set; }
    public string? LastError { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (State is ComponentState.Ready or ComponentState.Initializing) return;
        if (!_options.Enabled)
        {
            State = ComponentState.Unavailable;
            LastError = "Speech recognition disabled in configuration.";
            return;
        }
        State = ComponentState.Initializing;
        try
        {
            var entry = _catalog.Whisper.FirstOrDefault(w => w.Id == _options.Model)
                        ?? throw new FileNotFoundException($"Whisper model '{_options.Model}' is not in the catalog.");
            var path = Path.Combine(_paths.ModelsDirectory, entry.File);
            if (!File.Exists(path)) throw new FileNotFoundException("Speech recognition model not installed (Settings > Models).", path);

            var sw = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                RuntimeOptions.RuntimeLibraryOrder = _options.UseGpu
                    ? [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
                    : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
                _factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = _options.UseGpu, UseFlashAttention = true });
                _processor = _factory.CreateBuilder()
                    .WithThreads(_options.Threads)
                    .WithLanguage("auto")
                    .WithNoContext()
                    .WithSingleSegment()
                    .Build();
            }, cancellationToken).ConfigureAwait(false);

            var runtime = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
            Description = $"{entry.DisplayName} on {runtime}";
            State = ComponentState.Ready;
            _logger.LogInformation("Whisper ready in {Ms:F0} ms: {Description}", sw.Elapsed.TotalMilliseconds, Description);
        }
        catch (FileNotFoundException ex)
        {
            State = ComponentState.Unavailable;
            LastError = ex.Message;
            _logger.LogWarning("{Error}", LastError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State = ComponentState.Unavailable;
            LastError = $"Speech recognition unavailable: {ex.Message}";
            _logger.LogError(ex, "Whisper initialization failed");
        }
    }

    public async Task<Transcription> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var processor = _processor ?? throw new InvalidOperationException(LastError ?? "Speech recognition not initialized.");
            var sw = Stopwatch.StartNew();
            var audio = samples.ToArray();
            var duration = TimeSpan.FromSeconds((double)audio.Length / ISpeechToText.SampleRate);

            if (_options.AllowedLanguages.Count == 0)
            {
                var (any, _, detected) = await ProcessAsync(processor, audio, cancellationToken).ConfigureAwait(false);
                return new Transcription(any, detected, 0, duration, sw.Elapsed);
            }

            // Detecting the language reads the audio once more: while the user keeps speaking a language Whisper was
            // sure about, it is used directly. A transcript Whisper is unsure of, and every few utterances, detect again.
            if (_pinned is { } pinned && _pinnedUses < PinnedUtterances)
            {
                processor.ChangeLanguage(pinned.Language);
                var (text, confidence, _) = await ProcessAsync(processor, audio, cancellationToken).ConfigureAwait(false);
                if (text.Length == 0 || confidence >= MinPinnedConfidence)
                {
                    _pinnedUses++;
                    return new Transcription(text, pinned.Language, pinned.Probability, duration, sw.Elapsed);
                }
                _logger.LogInformation("Unsure transcript in {Language} (p={P:F2}): detecting the language again", pinned.Language, confidence);
            }

            var (language, probability) = await Task.Run(
                () => processor.DetectLanguageWithProbability(audio, _options.AllowedLanguages.ToArray()),
                cancellationToken).ConfigureAwait(false);
            processor.ChangeLanguage(language ?? "auto");
            var (result, _, segmentLanguage) = await ProcessAsync(processor, audio, cancellationToken).ConfigureAwait(false);
            language ??= segmentLanguage;
            _pinned = language != null && probability >= PinProbability ? (language, probability) : null;
            _pinnedUses = 0;
            return new Transcription(result, language, probability, duration, sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A detection at least this sure keeps its language for the next utterances.</summary>
    private const float PinProbability = 0.8f;
    /// <summary>Below this mean token probability, a transcript in the kept language is redone with detection.</summary>
    private const float MinPinnedConfidence = 0.5f;
    /// <summary>Utterances transcribed in the kept language before it is detected again.</summary>
    private const int PinnedUtterances = 5;

    /// <summary>The language kept from the last sure detection (guarded by <see cref="_gate"/>).</summary>
    private (string Language, float Probability)? _pinned;
    private int _pinnedUses;

    /// <returns>The text, the mean token probability of its segments, and the language Whisper reports.</returns>
    private static async Task<(string Text, float Confidence, string? Language)> ProcessAsync(
        WhisperProcessor processor, float[] audio, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        string? language = null;
        float confidence = 0;
        var segments = 0;
        await foreach (var segment in processor.ProcessAsync(audio, ct).ConfigureAwait(false))
        {
            text.Append(segment.Text);
            language ??= segment.Language;
            confidence += segment.Probability;
            segments++;
        }
        return (text.ToString().Trim(), segments == 0 ? 0 : confidence / segments, language);
    }

    public void Unload()
    {
        _gate.Wait();
        try
        {
            _processor?.Dispose();
            _factory?.Dispose();
            _processor = null;
            _factory = null;
            _pinned = null;
            State = ComponentState.NotInitialized;
            LastError = "Speech recognition model not installed (Settings > Models).";
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _gate.Dispose();
    }
}
