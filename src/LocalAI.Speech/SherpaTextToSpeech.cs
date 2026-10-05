using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace LocalAI.Speech;

/// <summary>
/// Offline neural TTS: Piper (VITS) voices executed in-process by sherpa-onnx on the CPU. One voice is active at a
/// time (the active assistant's). Engines are cached per voice and style, because expressiveness and rhythm are fixed
/// when an engine is created.
/// </summary>
public sealed class SherpaTextToSpeech : ITextToSpeech
{
    private const string NoVoiceError = "The assistant's voice is not installed (Settings > Models).";

    private readonly TextToSpeechOptions _options;
    private readonly LocalAiPaths _paths;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<SherpaTextToSpeech> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<EngineKey, Engine> _engines = [];
    private string? _voiceId;

    private sealed record EngineKey(string VoiceId, double Expressiveness, double Rhythm);
    private sealed record Engine(OfflineTts Tts, SemaphoreSlim Gate);

    public SherpaTextToSpeech(IOptions<LocalAiOptions> options, LocalAiPaths paths, ModelCatalog catalog, ILogger<SherpaTextToSpeech> logger)
    {
        _options = options.Value.TextToSpeech;
        _paths = paths;
        _catalog = catalog;
        _logger = logger;
    }

    public ComponentState State { get; private set; } = ComponentState.NotInitialized;
    public string? LastError { get; private set; }
    public VoiceStyle Style { get; set; } = VoiceStyle.Default;

    public VoiceInfo? Voice
    {
        get
        {
            var id = _voiceId;
            return id == null ? null : AvailableVoices.FirstOrDefault(v => v.Id == id);
        }
    }

    public IReadOnlyList<VoiceInfo> AvailableVoices =>
        _catalog.Tts
            .Where(v => Directory.Exists(Path.Combine(_paths.ModelsDirectory, v.Dir)))
            .Select(v => new VoiceInfo(v.Id, v.Language, string.IsNullOrEmpty(v.DisplayName) ? v.Id : v.DisplayName))
            .ToList();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (State is ComponentState.Ready or ComponentState.Initializing) return;
        if (!_options.Enabled)
        {
            State = ComponentState.Unavailable;
            LastError = "Speech output disabled in configuration.";
            return;
        }
        if (AvailableVoices.All(v => v.Id != _options.Voice))
        {
            State = ComponentState.Unavailable;
            LastError = NoVoiceError;
            _logger.LogWarning("{Error}", LastError);
            return;
        }
        State = ComponentState.Initializing;
        var sw = Stopwatch.StartNew();
        try
        {
            await SetVoiceAsync(_options.Voice, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("TTS ready in {Ms:F0} ms with voice {Voice}", sw.Elapsed.TotalMilliseconds, _options.Voice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            State = ComponentState.Unavailable;
            LastError = $"Speech output unavailable: {ex.Message}";
            _logger.LogError(ex, "Failed to load TTS voice {Voice}", _options.Voice);
        }
    }

    public async Task SetVoiceAsync(string voiceId, CancellationToken cancellationToken = default)
    {
        var style = Style;
        await Task.Run(() => GetEngine(new EngineKey(voiceId, style.Expressiveness, style.Rhythm)), cancellationToken).ConfigureAwait(false);
        _voiceId = voiceId;
        _options.Voice = voiceId;
        State = ComponentState.Ready;
        LastError = null;
        _logger.LogInformation("TTS voice set to {Voice}", voiceId);
    }

    public void RemoveVoice(string voiceId)
    {
        List<Engine> removed;
        lock (_gate)
        {
            var keys = _engines.Keys.Where(k => k.VoiceId == voiceId).ToList();
            removed = keys.Select(k => _engines[k]).ToList();
            foreach (var key in keys) _engines.Remove(key);
            if (_voiceId == voiceId)
            {
                _voiceId = null;
                State = ComponentState.Unavailable;
                LastError = NoVoiceError;
            }
        }
        foreach (var engine in removed)
        {
            engine.Gate.Wait();
            try { engine.Tts.Dispose(); }
            finally { engine.Gate.Release(); }
        }
        if (removed.Count > 0) _logger.LogInformation("TTS voice {Voice} released", voiceId);
    }

    public Task<AudioClip> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        var voiceId = _voiceId ?? throw new InvalidOperationException(LastError ?? "TTS not initialized.");
        return SynthesizeAsync(text, voiceId, Style, cancellationToken);
    }

    public async Task<AudioClip> SynthesizeAsync(string text, string voiceId, VoiceStyle style, CancellationToken cancellationToken = default)
    {
        var engine = await Task.Run(() => GetEngine(new EngineKey(voiceId, style.Expressiveness, style.Rhythm)), cancellationToken)
            .ConfigureAwait(false);
        await engine.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCached(engine)) throw new InvalidOperationException($"Voice '{voiceId}' was removed.");
            return await Task.Run(() =>
            {
                var audio = engine.Tts.Generate(text, (float)style.Speed, 0);
                try { return new AudioClip(PitchShifter.Shift(audio.Samples, audio.SampleRate, style.Pitch), audio.SampleRate); }
                finally { audio.Dispose(); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            engine.Gate.Release();
        }
    }

    private bool IsCached(Engine engine)
    {
        lock (_gate) return _engines.ContainsValue(engine);
    }

    private Engine GetEngine(EngineKey key)
    {
        lock (_gate)
        {
            if (_engines.TryGetValue(key, out var cached)) return cached;
        }
        var entry = _catalog.Tts.FirstOrDefault(v => v.Id == key.VoiceId)
                    ?? throw new ArgumentException($"TTS voice '{key.VoiceId}' is not in the catalog.");
        var engine = new Engine(CreateEngine(entry, key), new SemaphoreSlim(1, 1));
        List<Engine> stale;
        lock (_gate)
        {
            if (_engines.TryGetValue(key, out var raced))
            {
                engine.Tts.Dispose();
                return raced;
            }
            _engines[key] = engine;

            // Each engine holds a whole voice model: keep only the active voice's engine and the new one.
            var style = Style;
            var active = _voiceId == null ? null : new EngineKey(_voiceId, style.Expressiveness, style.Rhythm);
            var staleKeys = _engines.Keys.Where(k => k != key && k != active).ToList();
            stale = staleKeys.Select(k => _engines[k]).ToList();
            foreach (var k in staleKeys) _engines.Remove(k);
        }
        foreach (var old in stale)
        {
            old.Gate.Wait();
            try { old.Tts.Dispose(); }
            finally { old.Gate.Release(); }
        }
        return engine;
    }

    private OfflineTts CreateEngine(VoiceCatalogEntry entry, EngineKey key)
    {
        var dir = Path.Combine(_paths.ModelsDirectory, entry.Dir);
        var model = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.onnx").FirstOrDefault() : null;
        if (model == null) throw new FileNotFoundException($"Voice '{entry.Id}' is not installed (Settings > Models).", dir);
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = model;
        config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
        config.Model.Vits.DataDir = Path.Combine(dir, "espeak-ng-data");
        config.Model.Vits.NoiseScale = (float)key.Expressiveness;
        config.Model.Vits.NoiseScaleW = (float)key.Rhythm;
        config.Model.Vits.LengthScale = 1.0f;
        config.Model.NumThreads = _options.Threads;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        return new OfflineTts(config);
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
        }
    }
}
