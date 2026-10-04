using System.Diagnostics;
using LocalAI.Audio;
using LocalAI.Core.Assistant;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.LLM;
using LocalAI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace LocalAI.Integration.Tests;

/// <summary>Real-model tests: need the GPU, the downloaded models (scripts/setup.ps1) and ~10 GB free VRAM.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class BackendIntegrationTests(LocalAiFixture fx, ITestOutputHelper output)
{
    private T Get<T>() where T : notnull => fx.Services.GetRequiredService<T>();

    [Fact]
    public async Task Llm_loads_on_gpu_and_streams_incrementally()
    {
        var llm = Get<ILanguageModel>();
        Assert.Equal(LanguageModelState.Ready, llm.State);
        Assert.NotNull(llm.Info);
        output.WriteLine($"Model {llm.Info!.DisplayName} {llm.Info.Quantization} ctx={llm.Info.ContextSize} backend={llm.Info.Backend} " +
                         $"load={llm.Info.LoadTime.TotalSeconds:F2}s estVRAM={llm.Info.EstimatedVramMb} measuredVRAM={llm.Info.MeasuredVramMb}");
        Assert.Equal("CUDA", llm.Info.Backend);

        var chunks = new List<string>();
        GenerationStats? stats = null;
        await foreach (var c in llm.StreamAsync(
                           [new ChatMessage(ChatRole.User, "Count from 1 to 20 separated by spaces.")],
                           new GenerationOptions { Temperature = 0, MaxTokens = 100 }))
        {
            if (c.Text.Length > 0) chunks.Add(c.Text);
            stats ??= c.Stats;
        }
        var text = string.Concat(chunks);
        output.WriteLine(text);
        output.WriteLine($"TTFT {stats?.TimeToFirstTokenMs:F0} ms, {stats?.TokensPerSecond:F1} tok/s, prompt {stats?.PromptTokens} tok");
        Assert.True(chunks.Count > 5, "response should arrive as multiple streamed chunks");
        Assert.Contains("20", text);
        Assert.NotNull(stats);
        Assert.True(stats!.TokensPerSecond > 20, "GPU generation should exceed 20 tok/s");
    }

    [Fact]
    public async Task Llm_generation_stops_promptly_when_cancelled()
    {
        var llm = Get<ILanguageModel>();
        using var cts = new CancellationTokenSource();
        var received = 0;
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var c in llm.StreamAsync(
                               [new ChatMessage(ChatRole.User, "Write a very long essay about the history of computing.")],
                               new GenerationOptions { MaxTokens = 2000 }, cts.Token))
            {
                if (++received == 10)
                {
                    sw.Restart();
                    cts.Cancel();
                }
            }
        });
        output.WriteLine($"Stopped {sw.Elapsed.TotalMilliseconds:F0} ms after cancel, {received} chunks received");
        Assert.True(sw.ElapsedMilliseconds < 1000);

        // The engine must be immediately usable again (slot released).
        var next = await llm.GenerateAsync([new ChatMessage(ChatRole.User, "Say OK.")], new GenerationOptions { MaxTokens = 10, Temperature = 0 });
        Assert.False(string.IsNullOrWhiteSpace(next));
    }

    [Fact]
    public async Task Embeddings_rank_related_text_higher_across_languages()
    {
        var emb = Get<IEmbeddingService>();
        Assert.True(emb.IsAvailable);
        var memory = await emb.EmbedAsync("User prefers C# for software development.", EmbeddingPurpose.Document);
        var related = await emb.EmbedAsync("Qual linguagem devo usar para este aplicativo?", EmbeddingPurpose.Query);
        var unrelated = await emb.EmbedAsync("Che tempo fa domani a Roma?", EmbeddingPurpose.Query);
        var sRelated = VectorMath.Cosine(memory, related);
        var sUnrelated = VectorMath.Cosine(memory, unrelated);
        output.WriteLine($"dims={memory.Length} related={sRelated:F3} unrelated={sUnrelated:F3}");
        Assert.True(sRelated > sUnrelated + 0.05f);
    }

    [Theory]
    [InlineData("pt", "Olá, você pode me explicar como funciona a memória do computador?", new[] { "memória", "computador" })]
    [InlineData("it", "Ciao, puoi spiegarmi come funziona la memoria del computer?", new[] { "memoria", "computer" })]
    [InlineData("en", "Hello, can you explain how computer memory works?", new[] { "memory", "computer" })]
    public async Task Speech_round_trip_detects_language_and_transcribes(string language, string sentence, string[] keywords)
    {
        var tts = Get<ITextToSpeech>();
        var stt = Get<ISpeechToText>();
        Assert.Equal(ComponentState.Ready, tts.State);
        Assert.Equal(ComponentState.Ready, stt.State);
        output.WriteLine(stt.Description);

        var sw = Stopwatch.StartNew();
        var clip = await tts.SynthesizeAsync(sentence, language);
        var ttsMs = sw.Elapsed.TotalMilliseconds;
        var audio16k = StreamingResampler.Convert(clip.Samples, clip.SampleRate, ISpeechToText.SampleRate);

        var result = await stt.TranscribeAsync(audio16k);
        output.WriteLine($"[{language}] TTS {ttsMs:F0} ms for {clip.Duration.TotalSeconds:F1}s audio (voice {tts.GetVoice(language)?.Id}); " +
                         $"STT {result.ProcessingTime.TotalMilliseconds:F0} ms → lang={result.Language} p={result.LanguageProbability:F2}: {result.Text}");
        Assert.Equal(language, result.Language);
        foreach (var k in keywords) Assert.Contains(k, result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Voices_can_be_switched_and_speed_changes_duration()
    {
        var tts = Get<ITextToSpeech>();
        var installed = tts.AvailableVoices.Where(v => v.Language == "pt").ToList();
        output.WriteLine("Installed pt voices: " + string.Join(", ", installed.Select(v => v.Id)));
        Assert.Contains(installed, v => v.Id == "pt_BR-faber-medium");
        var original = tts.GetVoice("pt")!.Id;
        var other = installed.FirstOrDefault(v => v.Id != original);
        try
        {
            if (other != null)
            {
                await tts.SetVoiceAsync("pt", other.Id);
                Assert.Equal(other.Id, tts.GetVoice("pt")!.Id);
            }
            const string text = "Esta frase serve para medir a velocidade da fala.";
            tts.Speed = 1.0f;
            var normal = await tts.SynthesizeAsync(text, "pt");
            tts.Speed = 1.4f;
            var fast = await tts.SynthesizeAsync(text, "pt");
            output.WriteLine($"{tts.GetVoice("pt")!.Id}: 1.0x {normal.Duration.TotalSeconds:F2}s, 1.4x {fast.Duration.TotalSeconds:F2}s");
            Assert.True(fast.Duration < normal.Duration * 0.85);
        }
        finally
        {
            tts.Speed = 1.0f;
            await tts.SetVoiceAsync("pt", original);
        }
    }

    [Fact]
    public async Task Conversation_is_persisted_and_recovered_after_restart()
    {
        var session = Get<AssistantSession>();
        session.SelectConversation(null);
        var result = await session.SubmitAsync(new TurnRequest("Responda apenas com a palavra: banana.", InputSource.Text));
        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var conversationId = session.CurrentConversationId!.Value;

        // "Restart": a brand-new store over the same database file.
        var db = new SqliteDatabase(fx.Paths.DatabasePath, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteDatabase>.Instance);
        var store = new SqliteConversationStore(db);
        var conversations = await store.ListAsync();
        Assert.Contains(conversations, c => c.Id == conversationId);
        var messages = await store.GetMessagesAsync(conversationId);
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("pt", messages[0].Language);
        Assert.Contains("banana", messages[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(messages[1].Model);
        output.WriteLine($"Reply: {messages[1].Content}  meta={messages[1].MetadataJson}");
    }

    [Fact]
    public async Task Answers_in_the_language_of_the_user()
    {
        var session = Get<AssistantSession>();
        var detector = Get<LocalAI.Core.Language.ILanguageDetector>();
        foreach (var (lang, question) in new[]
                 {
                     ("pt", "O que é uma variável em programação? Responda em duas frases."),
                     ("it", "Che cos'è una variabile in programmazione? Rispondi in due frasi."),
                     ("en", "What is a variable in programming? Answer in two sentences."),
                 })
        {
            session.SelectConversation(null);
            var r = await session.SubmitAsync(new TurnRequest(question, InputSource.Text));
            var detected = detector.Detect(r.AssistantMessage!.Content);
            output.WriteLine($"[{lang}] → [{detected.Language} {detected.Confidence:F2}] {r.AssistantMessage.Content}");
            Assert.Equal(lang, detected.Language);
        }
    }

    [Fact]
    public async Task Long_term_memory_is_extracted_stored_and_recalled()
    {
        var memory = Get<MemoryService>();
        var session = Get<AssistantSession>();
        session.SelectConversation(null);

        var stored = await memory.ExtractAndStoreAsync(0, "I prefer writing software in C#. Please remember that.",
            "Got it, I'll keep in mind that you prefer C#.", CancellationToken.None);
        output.WriteLine("Stored: " + string.Join(" | ", stored.Select(m => m.Content)));
        var all = await memory.ListAsync(CancellationToken.None);
        Assert.Contains(all, m => m.Content.Contains("C#", StringComparison.Ordinal));

        var recalled = await memory.RecallAsync("What language should I use for this application?", CancellationToken.None);
        output.WriteLine("Recalled: " + string.Join(" | ", recalled.Select(r => $"{r.Score:F2} {r.Memory.Content}")));
        Assert.Contains(recalled, r => r.Memory.Content.Contains("C#", StringComparison.Ordinal));

        // And the assistant uses it.
        var reply = await session.SubmitAsync(new TurnRequest("What programming language should I use for my new desktop application? One sentence.", InputSource.Text));
        output.WriteLine("Reply: " + reply.AssistantMessage?.Content);
        Assert.Contains("C#", reply.AssistantMessage!.Content, StringComparison.Ordinal);

        // Duplicates are not stored twice.
        var again = await memory.RememberAsync("User prefers C# for software development.", null, CancellationToken.None);
        Assert.Null(again);
    }
}
