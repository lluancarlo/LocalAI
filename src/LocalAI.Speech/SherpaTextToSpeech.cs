using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace LocalAI.Speech;

/// <summary>
/// Offline neural TTS: Piper (VITS) voices executed in-process by sherpa-onnx on the CPU.
/// One active voice per language, chosen from the detected language of the conversation; voices can be switched at
/// runtime (engines are loaded on demand and cached).
/// </summary>
public sealed class SherpaTextToSpeech : ITextToSpeech
{
    private readonly TextToSpeechOptions _options;
    private readonly LocalAiPaths _paths;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<SherpaTextToSpeech> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Engine> _engines = new(StringComparer.OrdinalIgnoreCase);   // by voice id
    private readonly Dictionary<string, string> _byLanguage = new(StringComparer.OrdinalIgnoreCase); // language → voice id

    private sealed record Engine(VoiceInfo Info, OfflineTts Tts, SemaphoreSlim Gate);

    public SherpaTextToSpeech(IOptions<LocalAiOptions> options, LocalAiPaths paths, ModelCatalog catalog, ILogger<SherpaTextToSpeech> logger)
    {
        _options = options.Value.TextToSpeech;
        _paths = paths;
        _catalog = catalog;
        _logger = logger;
    }

    public ComponentState State { get; private set; } = ComponentState.NotInitialized;
    public string? LastError { get; private set; }

    public IReadOnlyList<VoiceInfo> Voices
    {
        get
        {
            lock (_gate)
                return _byLanguage.OrderBy(kv => kv.Key).Select(kv => _engines[kv.Value].Info with { Language = kv.Key }).ToList();
        }
    }

    public IReadOnlyList<VoiceInfo> AvailableVoices =>
        _catalog.Tts
            .Where(v => Directory.Exists(Path.Combine(_paths.ModelsDirectory, v.Dir)))
            .Select(v => new VoiceInfo(v.Id, v.Language, string.IsNullOrEmpty(v.DisplayName) ? v.Id : v.DisplayName))
            .ToList();

    public float Speed
    {
        get => _options.Speed;
        set => _options.Speed = Math.Clamp(value, 0.5f, 2.0f);
    }

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
                try { Activate(language, voiceId); }
                catch (Exception ex) { _logger.LogError(ex, "Failed to load TTS voice {Voice}", voiceId); }
            }
        }, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_byLanguage.Count == 0)
            {
                State = ComponentState.Unavailable;
                LastError = "No TTS voices installed (run scripts/setup.ps1).";
                _logger.LogError("{Error}", LastError);
                return;
            }
        }
        State = ComponentState.Ready;
        _logger.LogInformation("TTS ready in {Ms:F0} ms with voices: {Voices}", sw.Elapsed.TotalMilliseconds,
            string.Join(", ", Voices.Select(v => $"{v.Language}={v.Id}")));
    }

    public async Task SetVoiceAsync(string language, string voiceId, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Activate(language, voiceId), cancellationToken).ConfigureAwait(false);
        _options.Voices[language] = voiceId;
        if (State != ComponentState.Ready)
        {
            State = ComponentState.Ready;
            LastError = null;
        }
        _logger.LogInformation("TTS voice for {Language} set to {Voice}", language, voiceId);
    }

    private void Activate(string language, string voiceId)
    {
        lock (_gate)
        {
            if (_engines.ContainsKey(voiceId))
            {
                _byLanguage[language] = voiceId;
                return;
            }
        }
        var entry = _catalog.Tts.FirstOrDefault(v => v.Id == voiceId)
                    ?? throw new ArgumentException($"TTS voice '{voiceId}' is not in the catalog.");
        var engine = new Engine(new VoiceInfo(entry.Id, entry.Language, entry.DisplayName), CreateEngine(entry), new SemaphoreSlim(1, 1));
        lock (_gate)
        {
            _engines[voiceId] = engine;
            _byLanguage[language] = voiceId;
        }
    }

    private OfflineTts CreateEngine(VoiceCatalogEntry entry)
    {
        var dir = Path.Combine(_paths.ModelsDirectory, entry.Dir);
        var model = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.onnx").FirstOrDefault() : null;
        if (model == null) throw new FileNotFoundException($"Voice '{entry.Id}' is not installed (run scripts/setup.ps1 -AllVoices).", dir);
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

    private Engine? Resolve(string? language)
    {
        lock (_gate)
        {
            if (language != null && _byLanguage.TryGetValue(language, out var id)) return _engines[id];
            if (_byLanguage.TryGetValue(_options.FallbackLanguage, out var fallback)) return _engines[fallback];
            return _byLanguage.Count > 0 ? _engines[_byLanguage.Values.First()] : null;
        }
    }

    public async Task<AudioClip> SynthesizeAsync(string text, string? language, CancellationToken cancellationToken = default)
    {
        var engine = Resolve(language) ?? throw new InvalidOperationException(LastError ?? "TTS not initialized.");
        await engine.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var speed = Speed;
            return await Task.Run(() =>
            {
                var audio = engine.Tts.Generate(text, speed, 0);
                try { return new AudioClip(audio.Samples, audio.SampleRate); }
                finally { audio.Dispose(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            engine.Gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var e in _engines.Values)
            {
                e.Tts.Dispose();
                e.Gate.Dispose();
            }
            _engines.Clear();
            _byLanguage.Clear();
        }
    }
}
