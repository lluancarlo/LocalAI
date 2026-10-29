using System.Diagnostics;
using LocalAI.Audio;
using LocalAI.Core.Assistant;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LocalAI.Integration.Tests;

/// <summary>Prints stage-by-stage timings on the real hardware.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Benchmark")]
public sealed class PerformanceBenchmarks(LocalAiFixture fx, ITestOutputHelper output)
{
    private T Get<T>() where T : notnull => fx.Services.GetRequiredService<T>();

    private static string Median(List<double> v) { v.Sort(); return v[v.Count / 2].ToString("F0"); }

    [Fact]
    public async Task Speech_recognition_and_synthesis()
    {
        var tts = Get<ITextToSpeech>();
        var stt = Get<ISpeechToText>();
        var sentences = new (string Lang, string Short, string Long)[]
        {
            ("pt", "Qual é a previsão do tempo para amanhã?",
                "Eu estou desenvolvendo um aplicativo em C sharp e gostaria de entender melhor como funciona o modelo de programação assíncrona, principalmente quando uso tarefas em paralelo."),
            ("it", "Che tempo farà domani a Milano?",
                "Sto sviluppando un'applicazione in C sharp e vorrei capire meglio come funziona il modello di programmazione asincrona, soprattutto quando uso attività in parallelo."),
            ("en", "What will the weather be like tomorrow?",
                "I am building an application in C sharp and I would like to better understand how the asynchronous programming model works, especially when I run tasks in parallel."),
        };
        foreach (var (lang, shortText, longText) in sentences)
        {
            foreach (var text in new[] { shortText, longText })
            {
                var ttsTimes = new List<double>();
                AudioClip clip = null!;
                for (var i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    clip = await tts.SynthesizeAsync(text, LocalAiFixture.VoiceFor(lang), VoiceStyle.Default);
                    ttsTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                var audio = StreamingResampler.Convert(clip.Samples, clip.SampleRate, ISpeechToText.SampleRate);
                var sttTimes = new List<double>();
                Transcription t = null!;
                for (var i = 0; i < 3; i++)
                {
                    t = await stt.TranscribeAsync(audio);
                    sttTimes.Add(t.ProcessingTime.TotalMilliseconds);
                }
                output.WriteLine($"[{lang}] {clip.Duration.TotalSeconds,4:F1}s audio | TTS median {Median(ttsTimes)} ms (RTF {double.Parse(Median(ttsTimes)) / clip.Duration.TotalMilliseconds:F3}) | " +
                                 $"STT median {Median(sttTimes)} ms (lang {t.Language})");
            }
        }
    }

    [Fact]
    public async Task Llm_latency_and_throughput()
    {
        var llm = Get<ILanguageModel>();
        var longContext = string.Join("\n", Enumerable.Range(1, 60).Select(i => $"Note {i}: the quick brown fox jumps over the lazy dog while the compiler optimizes loop {i}."));
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a helpful assistant."),
            new(ChatRole.User, longContext + "\n\nSummarize the notes above in one sentence."),
        };
        for (var run = 0; run < 3; run++)
        {
            GenerationStats? stats = null;
            await foreach (var c in llm.StreamAsync(messages, new GenerationOptions { MaxTokens = 300, Temperature = 0.7f }))
                stats ??= c.Stats;
            output.WriteLine($"run {run + 1}: prompt {stats!.PromptTokens} tok (+{stats.CachedPromptTokens} cached) in {stats.PromptMs:F0} ms, " +
                             $"TTFT {stats.TimeToFirstTokenMs:F0} ms, {stats.GeneratedTokens} tok at {stats.TokensPerSecond:F1} tok/s");
        }

        var longAnswer = new List<ChatMessage> { new(ChatRole.User, "Write a 400-word essay about compilers.") };
        GenerationStats? s2 = null;
        await foreach (var c in llm.StreamAsync(longAnswer, new GenerationOptions { MaxTokens = 600 })) s2 ??= c.Stats;
        output.WriteLine($"long generation: {s2!.GeneratedTokens} tok at {s2.TokensPerSecond:F1} tok/s, TTFT {s2.TimeToFirstTokenMs:F0} ms");
    }

    [Fact]
    public async Task Time_to_first_spoken_audio()
    {
        var session = Get<AssistantSession>();
        var results = new List<double>();
        foreach (var (lang, q) in new[] { ("pt", "Me dê uma dica rápida de produtividade."), ("it", "Dammi un consiglio veloce sulla produttività."), ("en", "Give me a quick productivity tip.") })
        {
            session.SelectConversation(null);
            var tcs = new TaskCompletionSource<TimeSpan>();
            void Handler(object? s, TimeSpan latency) => tcs.TrySetResult(latency);
            session.Speech.SpeakingStarted += Handler;
            var sw = Stopwatch.StartNew();
            var turn = session.SubmitAsync(new TurnRequest(q, InputSource.Voice, lang, Speak: true));
            var first = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var total = sw.Elapsed;
            session.Speech.SpeakingStarted -= Handler;
            session.CancelCurrentTurn();
            await turn;
            results.Add(total.TotalMilliseconds);
            output.WriteLine($"[{lang}] submit → first audio queued: {total.TotalMilliseconds:F0} ms (from generation start {first.TotalMilliseconds:F0} ms)");
        }
    }
}
