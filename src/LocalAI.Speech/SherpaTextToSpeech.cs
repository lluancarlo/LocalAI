using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

namespace LocalAI.Speech;

/// <summary>
/// Offline neural TTS: the Kokoro model executed in-process by sherpa-onnx on the CPU. Every voice is a speaker of the
/// same model; one is active at a time (the active assistant's). The language the text is read in (espeak-ng) is fixed
/// when an engine is created, so there is one engine per language, shared by the voices of that language.
/// </summary>
public sealed class SherpaTextToSpeech : ITextToSpeech
{
    private const string NoVoiceError = "The voices are not installed (Settings > Models).";

    private readonly TextToSpeechOptions _options;
    private readonly LocalAiPaths _paths;
    private readonly ModelCatalog _catalog;
    private readonly ILogger<SherpaTextToSpeech> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Engine> _engines = []; // by espeak-ng language
    private string? _voiceId;

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

    /// <summary>Every voice of the catalog once the model is installed.</summary>
    public IReadOnlyList<VoiceInfo> AvailableVoices =>
        Directory.Exists(ModelDirectory)
            ? _catalog.Voices.Select(v => new VoiceInfo(v.Id, v.Language, v.Label)).ToList()
            : [];

    private string ModelDirectory => Path.Combine(_paths.ModelsDirectory, _catalog.Tts.Dir);

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
        // The new voice is current right away: speech asked for while it loads waits for it instead of using the old one.
        _voiceId = voiceId;
        _options.Voice = voiceId;
        await Task.Run(() => GetEngine(FindVoice(voiceId).Lang), cancellationToken).ConfigureAwait(false);
        State = ComponentState.Ready;
        LastError = null;
        _logger.LogInformation("TTS voice set to {Voice}", voiceId);
    }

    public void Release()
    {
        List<Engine> removed;
        lock (_gate)
        {
            removed = _engines.Values.ToList();
            _engines.Clear();
            _voiceId = null;
            State = ComponentState.NotInitialized;
            LastError = "Speech output is off: the voice loads when the AI is turned on.";
        }
        foreach (var engine in removed)
        {
            engine.Gate.Wait();
            try { engine.Tts.Dispose(); }
            finally { engine.Gate.Release(); }
        }
        if (removed.Count > 0) _logger.LogInformation("TTS voices released");
    }

    public void ReleaseUnused()
    {
        List<Engine> removed;
        lock (_gate)
        {
            var active = _catalog.Voices.FirstOrDefault(v => v.Id == _voiceId)?.Lang;
            var unused = _engines.Keys.Where(k => k != active).ToList();
            removed = unused.Select(k => _engines[k]).ToList();
            foreach (var k in unused) _engines.Remove(k);
        }
        foreach (var engine in removed)
        {
            engine.Gate.Wait();
            try { engine.Tts.Dispose(); }
            finally { engine.Gate.Release(); }
        }
        if (removed.Count > 0) _logger.LogInformation("Released {Count} voice engine(s) not in use", removed.Count);
    }

    public Task<AudioClip> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        var voiceId = _voiceId ?? throw new InvalidOperationException(LastError ?? "TTS not initialized.");
        return SynthesizeAsync(text, voiceId, Style, cancellationToken);
    }

    public async Task<AudioClip> SynthesizeAsync(string text, string voiceId, VoiceStyle style, CancellationToken cancellationToken = default)
    {
        var voice = FindVoice(voiceId);
        var engine = await Task.Run(() => GetEngine(voice.Lang), cancellationToken).ConfigureAwait(false);
        await engine.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCached(engine)) throw new InvalidOperationException("The voice was released.");
            return await Task.Run(() =>
            {
                var audio = engine.Tts.Generate(text, (float)style.Speed, voice.Speaker);
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

    private VoiceCatalogEntry FindVoice(string voiceId) =>
        _catalog.Voices.FirstOrDefault(v => v.Id == voiceId)
        ?? throw new ArgumentException($"TTS voice '{voiceId}' is not in the catalog.");

    private Engine GetEngine(string lang)
    {
        lock (_gate)
        {
            if (_engines.TryGetValue(lang, out var cached)) return cached;
        }
        var engine = new Engine(CreateEngine(lang), new SemaphoreSlim(1, 1));
        List<Engine> stale;
        lock (_gate)
        {
            if (_engines.TryGetValue(lang, out var raced))
            {
                engine.Tts.Dispose();
                return raced;
            }
            _engines[lang] = engine;

            // Each engine holds the whole model: keep only the active voice's engine and the new one.
            var active = _catalog.Voices.FirstOrDefault(v => v.Id == _voiceId)?.Lang;
            var staleKeys = _engines.Keys.Where(k => k != lang && k != active).ToList();
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

    private OfflineTts CreateEngine(string lang)
    {
        var dir = ModelDirectory;
        var model = Path.Combine(dir, "model.onnx");
        if (!File.Exists(model)) throw new FileNotFoundException("The voices are not installed (Settings > Models).", model);
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = model;
        config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
        config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
        config.Model.Kokoro.DataDir = Path.Combine(dir, "espeak-ng-data");
        config.Model.Kokoro.Lang = lang;
        config.Model.Kokoro.LengthScale = 1.0f;
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
