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

            string? language = null;
            float probability = 0;
            if (_options.AllowedLanguages.Count > 0)
            {
                (language, probability) = await Task.Run(
                    () => processor.DetectLanguageWithProbability(audio, _options.AllowedLanguages.ToArray()),
                    cancellationToken).ConfigureAwait(false);
                processor.ChangeLanguage(language ?? "auto");
            }

            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
                language ??= segment.Language;
            }

            return new Transcription(
                text.ToString().Trim(),
                language,
                probability,
                TimeSpan.FromSeconds((double)audio.Length / ISpeechToText.SampleRate),
                sw.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
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
