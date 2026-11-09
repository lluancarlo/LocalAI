using System.Diagnostics;
using LocalAI.Configuration;
using LocalAI.Core.Speech;
using LocalAI.Models;
using LocalAI.Speech;
using LocalAI.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace LocalAI.Integration.Tests;

/// <summary>
/// The Kokoro voices through the app's own engine: every language the app knows is spoken (Portuguese and Italian are
/// not official in sherpa-onnx and read through espeak-ng, which a sherpa-onnx update could drop), and how fast the
/// first words come on this computer. Downloads the model from Hugging Face into the published app's models folder when
/// missing and writes every sample as a .wav to listen to.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class KokoroBenchmarks(ITestOutputHelper output)
{
    // The first chunk the app speaks is short (SentenceChunker cuts it at the first comma): that is the latency heard.
    private static readonly (string Lang, string First, string Long)[] Sentences =
    [
        ("pt", "Claro, posso ajudar.",
            "Amanhã de manhã vai chover em São Paulo, então é melhor levar um guarda-chuva e sair um pouco mais cedo de casa."),
        ("it", "Certo, posso aiutarti.",
            "Domani mattina pioverà a Milano, quindi è meglio portare un ombrello e uscire di casa un po' prima del solito."),
        ("en", "Sure, I can help.",
            "Tomorrow morning it will rain in Seattle, so it is better to take an umbrella and leave home a little earlier than usual."),
    ];

    private static string ModelsDirectory => Path.Combine(TestPaths.PublishedApp, "data", "models");
    private static string SamplesDirectory => Path.Combine(TestPaths.Root, "kokoro-samples");

    [Fact]
    public async Task Every_language_is_spoken_and_latency_is_measured()
    {
        Directory.CreateDirectory(SamplesDirectory);
        var paths = new LocalAiPaths(TestPaths.PublishedApp, TestPaths.New("kokoro")) { ModelsDirectory = ModelsDirectory };
        var catalog = ModelCatalog.LoadDefault();
        var library = new ModelLibrary(catalog, paths, new ModelDownloader());
        var package = library.Packages.Single(p => p.Kind == ModelKind.Voice);
        if (!library.IsInstalled(package))
        {
            output.WriteLine($"Downloading {package.Id}…");
            await library.InstallAsync(package, null, CancellationToken.None);
        }
        output.WriteLine($"CPU threads available: {Environment.ProcessorCount}. Samples: {SamplesDirectory}");

        using var tts = new SherpaTextToSpeech(Options.Create(new LocalAiOptions()), paths, catalog, NullLogger<SherpaTextToSpeech>.Instance);
        foreach (var (lang, first, longText) in Sentences)
        {
            var voice = catalog.Voices.First(v => v.Default && v.Language == lang);
            var sw = Stopwatch.StartNew();
            await tts.SetVoiceAsync(voice.Id);
            var load = sw.Elapsed.TotalMilliseconds;

            // The first synthesis after loading is slower (the runtime warms up); the app pays it once per language.
            sw.Restart();
            await tts.SynthesizeAsync(first);
            var cold = sw.Elapsed.TotalMilliseconds;

            var firstTimes = new List<double>();
            AudioClip firstAudio = null!;
            for (var i = 0; i < 3; i++)
            {
                sw.Restart();
                firstAudio = await tts.SynthesizeAsync(first);
                firstTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
            sw.Restart();
            var longAudio = await tts.SynthesizeAsync(longText);
            var longMs = sw.Elapsed.TotalMilliseconds;

            Save(firstAudio, $"{lang}-{voice.Id}-first.wav");
            Save(longAudio, $"{lang}-{voice.Id}-long.wav");
            firstTimes.Sort();
            output.WriteLine($"[{lang}] {voice.Label,-26} load {load,6:F0} ms | cold first {cold,6:F0} ms | first chunk {firstTimes[1],5:F0} ms | " +
                             $"long {longAudio.Duration.TotalSeconds,4:F1}s audio in {longMs,6:F0} ms (RTF {longMs / 1000 / longAudio.Duration.TotalSeconds:F3})");

            // A language espeak-ng no longer reads comes out silent or nearly so.
            Assert.True(longAudio.Duration > TimeSpan.FromSeconds(3), $"{lang}: {longAudio.Duration.TotalSeconds:F1}s of audio");
            Assert.Contains(longAudio.Samples, s => Math.Abs(s) > 0.05f);
        }
    }

    /// <summary>
    /// Every voice of the catalog speaks its language's sample (what ▶ plays). A "lang" espeak-ng does not know (e.g.
    /// "en-gb": British English is "en") gives no audio at all.
    /// </summary>
    [Fact]
    public async Task Every_voice_of_the_catalog_speaks()
    {
        var paths = new LocalAiPaths(TestPaths.PublishedApp, TestPaths.New("kokoro-voices")) { ModelsDirectory = ModelsDirectory };
        var catalog = ModelCatalog.LoadDefault();
        var languages = LanguageData.LoadDefault();
        using var tts = new SherpaTextToSpeech(Options.Create(new LocalAiOptions()), paths, catalog, NullLogger<SherpaTextToSpeech>.Instance);
        var silent = new List<string>();
        foreach (var voice in catalog.Voices)
        {
            try
            {
                var clip = await tts.SynthesizeAsync(languages.Languages[voice.Language].VoiceSample, voice.Id, VoiceStyle.Default);
                if (clip.Duration < TimeSpan.FromSeconds(1)) silent.Add($"{voice.Id}: {clip.Duration.TotalSeconds:F1}s");
            }
            catch (InvalidOperationException ex)
            {
                silent.Add($"{voice.Id}: {ex.Message}");
            }
        }
        Assert.Empty(silent);
    }

    [Fact]
    public async Task A_preview_with_the_ai_off_leaves_no_voice_loaded()
    {
        var paths = new LocalAiPaths(TestPaths.PublishedApp, TestPaths.New("kokoro-preview")) { ModelsDirectory = ModelsDirectory };
        var catalog = ModelCatalog.LoadDefault();
        var logs = new List<string>();
        using var tts = new SherpaTextToSpeech(Options.Create(new LocalAiOptions()), paths, catalog, new ListLogger(logs));
        var dora = catalog.Voices.First(v => v.Id == "pf_dora");

        var clip = await tts.SynthesizeAsync("Olá!", dora.Id, VoiceStyle.Default); // the preview, no active voice
        Assert.True(clip.Samples.Length > 0);
        tts.ReleaseUnused();
        Assert.Contains(logs, l => l.Contains("Released 1 voice engine", StringComparison.Ordinal));
        Assert.Null(tts.Voice);

        // Played again: the engine loads again.
        Assert.True((await tts.SynthesizeAsync("Olá!", dora.Id, VoiceStyle.Default)).Samples.Length > 0);
    }

    private sealed class ListLogger(List<string> lines) : Microsoft.Extensions.Logging.ILogger<SherpaTextToSpeech>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => lines.Add(formatter(state, exception));
    }

    /// <summary>16-bit mono PCM .wav.</summary>
    private static void Save(AudioClip clip, string name)
    {
        using var writer = new BinaryWriter(File.Create(Path.Combine(SamplesDirectory, name)));
        var bytes = clip.Samples.Length * 2;
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(clip.SampleRate); writer.Write(clip.SampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes);
        foreach (var s in clip.Samples) writer.Write((short)Math.Clamp(s * short.MaxValue, short.MinValue, short.MaxValue));
    }
}
