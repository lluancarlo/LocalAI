using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace LocalAI.Speech;

/// <summary>
/// Offline neural TTS: Piper (VITS) voices executed in-process by sherpa-onnx on the CPU.
/// One voice per language; the voice is chosen from the detected language of the conversation.
/// </summary>
public sealed class SherpaTextToSpeech : ITextToSpeech
{
    private readonly TextToSpeechOptions _options;
    private readonly LocalAiPaths _paths;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<SherpaTextToSpeech> _logger;
    private readonly Dictionary<string, (VoiceInfo Info, OfflineTts Engine, SemaphoreSlim Gate)> _voices = new(StringComparer.OrdinalIgnoreCase);

    public SherpaTextToSpeech(IOptions<LocalAiOptions> options, LocalAiPaths paths, ModelCatalog catalog, ILogger<SherpaTextToSpeech> logger)
    {
        _options = options.Value.TextToSpeech;
        _paths = paths;
        _catalog = catalog;
        _logger = logger;
    }

    public ComponentState State { get; private set; } = ComponentState.NotInitialized;
    public string? LastError { get; private set; }
    public IReadOnlyList<VoiceInfo> Voices => _voices.Values.Select(v => v.Info).ToList();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (State is ComponentState.Ready or ComponentState.Initializing) return;
        if (!_options.Enabled)
        {
            State = ComponentState.Unavailable;
            LastError = "Speech output disabled in configuration.";
            return;
        }
        State = ComponentState.Initializing;
        var sw = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            foreach (var (language, voiceId) in _options.Voices)
            {
                var entry = _catalog.Tts.FirstOrDefault(v => v.Id == voiceId);
                if (entry == null) { _logger.LogWarning("TTS voice {Voice} not in catalog", voiceId); continue; }
                try
                {
                    _voices[language] = (new VoiceInfo(entry.Id, language, entry.DisplayName), CreateEngine(entry), new SemaphoreSlim(1, 1));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load TTS voice {Voice}", voiceId);
                }
            }
        }, cancellationToken).ConfigureAwait(false);

        if (_voices.Count == 0)
        {
            State = ComponentState.Unavailable;
            LastError = "No TTS voices installed (run scripts/setup.ps1).";
            _logger.LogError("{Error}", LastError);
            return;
        }
        State = ComponentState.Ready;
        _logger.LogInformation("TTS ready in {Ms:F0} ms with voices: {Voices}", sw.Elapsed.TotalMilliseconds,
            string.Join(", ", _voices.Values.Select(v => $"{v.Info.Language}={v.Info.Id}")));
    }

    private OfflineTts CreateEngine(VoiceCatalogEntry entry)
    {
        var dir = Path.Combine(_paths.ModelsDirectory, entry.Dir);
        var model = Directory.GetFiles(dir, "*.onnx").FirstOrDefault()
                    ?? throw new FileNotFoundException($"No .onnx model in {dir}");
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = model;
        config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
        config.Model.Vits.DataDir = Path.Combine(dir, "espeak-ng-data");
        config.Model.Vits.NoiseScale = 0.667f;
        config.Model.Vits.NoiseScaleW = 0.8f;
        config.Model.Vits.LengthScale = 1.0f;
        config.Model.NumThreads = _options.Threads;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        return new OfflineTts(config);
    }

    public VoiceInfo? GetVoice(string? language) => Resolve(language)?.Info;

    private (VoiceInfo Info, OfflineTts Engine, SemaphoreSlim Gate)? Resolve(string? language)
    {
        if (language != null && _voices.TryGetValue(language, out var v)) return v;
        if (_voices.TryGetValue(_options.FallbackLanguage, out var f)) return f;
        return _voices.Count > 0 ? _voices.Values.First() : null;
    }

    public async Task<AudioClip> SynthesizeAsync(string text, string? language, CancellationToken cancellationToken = default)
    {
        var voice = Resolve(language) ?? throw new InvalidOperationException(LastError ?? "TTS not initialized.");
        await voice.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var audio = voice.Engine.Generate(text, _options.Speed, 0);
                try { return new AudioClip(audio.Samples, audio.SampleRate); }
                finally { audio.Dispose(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            voice.Gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var v in _voices.Values)
        {
            v.Engine.Dispose();
            v.Gate.Dispose();
        }
        _voices.Clear();
    }
}
