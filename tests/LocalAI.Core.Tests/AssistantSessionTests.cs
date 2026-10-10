using System.Text.Json;
using LocalAI.Core.Assistant;
using LocalAI.Core.Conversations;
using LocalAI.Core.Language;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

public sealed class AssistantSessionTests
{
    private static readonly Configuration.LanguageData Languages = Configuration.LanguageData.LoadDefault();
    private readonly FakeLanguageModel _llm = new();
    private readonly LocalAI.Core.Assistants.AssistantContext _assistant = new();
    private readonly InMemoryConversationStore _store = new();
    private readonly InMemoryMemoryStore _memories = new();
    private readonly NoMemoryRetriever _retriever = new();
    private readonly FakeTts _tts = new();
    private readonly FakePlayer _player = new();

    private AssistantSession CreateSession(Action<Configuration.LocalAiOptions>? configure = null)
    {
        var options = TestOptions.Create(o =>
        {
            o.Memory.AutoExtract = false;
            configure?.Invoke(o);
        });
        var memory = new MemoryService(_memories, _retriever, new FakeEmbeddings(), _llm, options, NullLogger<MemoryService>.Instance);
        var speech = new SpeechOutput(_tts, _player, Languages, NullLogger<SpeechOutput>.Instance);
        return new AssistantSession(_llm, _store, new PromptBuilder(options, _assistant), new HeuristicLanguageDetector(Languages), memory, speech, options,
            NullLogger<AssistantSession>.Instance);
    }

    [Fact]
    public async Task Creates_conversation_and_persists_both_messages()
    {
        using var session = CreateSession();
        var deltas = new List<string>();
        session.AssistantDelta += (_, e) => deltas.Add(e.Delta);

        var result = await session.SubmitAsync(new TurnRequest("Explique async/await em C#, por favor.", InputSource.Text));

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(["Hello", " there", "."], deltas);
        var conversation = Assert.Single(await _store.ListAsync());
        Assert.Equal("Explique async/await em C#, por favor.", conversation.Title);
        var messages = await _store.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("pt", messages[0].Language);
        Assert.Equal("Hello there.", messages[1].Content);
        Assert.Equal("fake", messages[1].Model);
        Assert.NotNull(result.Stats);
    }

    [Fact]
    public async Task Continues_existing_conversation_with_history_in_prompt()
    {
        using var session = CreateSession();
        await session.SubmitAsync(new TurnRequest("My name is Ana.", InputSource.Text));
        await session.SubmitAsync(new TurnRequest("What is my name?", InputSource.Text));

        Assert.Single(await _store.ListAsync());
        var prompt = _llm.Requests[^1];
        Assert.Equal(ChatRole.System, prompt[0].Role);
        Assert.Contains(prompt, m => m.Role == ChatRole.User && m.Content.Contains("My name is Ana.", StringComparison.Ordinal));
        Assert.Contains(prompt, m => m.Role == ChatRole.Assistant);
        Assert.Equal("What is my name?", prompt[^1].Content);
    }

    [Fact]
    public async Task Selected_text_is_stored_with_the_message_and_quoted_in_the_prompt()
    {
        using var session = CreateSession();
        await session.SubmitAsync(new TurnRequest("What does this mean?", InputSource.Voice, "en", SelectedText: "ON DELETE CASCADE"));

        var prompt = _llm.Requests[^1];
        Assert.Contains("ON DELETE CASCADE", prompt[^1].Content, StringComparison.Ordinal);
        Assert.Contains("What does this mean?", prompt[^1].Content, StringComparison.Ordinal);
        Assert.True(prompt[^1].Content.IndexOf("ON DELETE CASCADE", StringComparison.Ordinal) <
                    prompt[^1].Content.IndexOf("What does this mean?", StringComparison.Ordinal));
        var user = (await _store.GetMessagesAsync(session.CurrentConversationId!.Value))[0];
        Assert.Equal("What does this mean?", user.Content); // the stored text is what the user said
        Assert.Equal("ON DELETE CASCADE", user.SelectedText);
    }

    [Fact]
    public async Task A_follow_up_question_still_sees_the_earlier_selection()
    {
        using var session = CreateSession();
        await session.SubmitAsync(new TurnRequest("Translate this.", InputSource.Voice, "en", SelectedText: "Buongiorno a tutti"));
        await session.SubmitAsync(new TurnRequest("And more formally?", InputSource.Voice, "en"));

        var prompt = _llm.Requests[^1];
        Assert.Contains(prompt, m => m.Role == ChatRole.User && m.Content.Contains("Buongiorno a tutti", StringComparison.Ordinal));
        Assert.Equal("And more formally?", prompt[^1].Content);
    }

    [Fact]
    public async Task Cancellation_stops_generation_and_keeps_partial_reply_marked_interrupted()
    {
        using var session = CreateSession();
        _llm.TokenDelay = TimeSpan.FromMilliseconds(30);
        _llm.Responder = _ => Enumerable.Range(0, 200).Select(i => $" w{i}");
        var received = 0;
        session.AssistantDelta += (_, _) => { if (Interlocked.Increment(ref received) == 5) session.CancelCurrentTurn(); };

        var result = await session.SubmitAsync(new TurnRequest("Write a long story.", InputSource.Text));

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.InRange(received, 5, 7);
        var stored = result.AssistantMessage!;
        using var meta = JsonDocument.Parse(stored.MetadataJson!);
        Assert.True(meta.RootElement.GetProperty("interrupted").GetBoolean());
        Assert.True(_player.Stops > 0, "playback must be stopped on cancel");
    }

    [Fact]
    public async Task New_turn_cancels_the_previous_one()
    {
        using var session = CreateSession();
        _llm.TokenDelay = TimeSpan.FromMilliseconds(20);
        _llm.Responder = m => m[^1].Content.Contains("first", StringComparison.Ordinal)
            ? Enumerable.Repeat(" x", 500)
            : ["done"];

        var first = session.SubmitAsync(new TurnRequest("first question", InputSource.Text));
        await Task.Delay(150);
        var second = await session.SubmitAsync(new TurnRequest("second question", InputSource.Text));

        Assert.Equal(TurnOutcome.Cancelled, (await first).Outcome);
        Assert.Equal(TurnOutcome.Completed, second.Outcome);
    }

    [Fact]
    public async Task Fails_gracefully_when_model_is_not_loaded()
    {
        _llm.State = LanguageModelState.Failed;
        _llm.LastError = "Model not installed.";
        using var session = CreateSession();
        var result = await session.SubmitAsync(new TurnRequest("Hi", InputSource.Text));
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal("Model not installed.", result.Error);
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public async Task Spoken_turn_streams_sentences_to_tts()
    {
        using var session = CreateSession();
        _llm.Responder = _ => ["Sure", ". Async", " and await", " work", " together", "."];

        var result = await session.SubmitAsync(new TurnRequest("Explain async", InputSource.Voice, Language: "en", Speak: true));

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(["Sure.", "Async and await work together."], _tts.Spoken);
        Assert.Equal(2, _player.Enqueued);
        // Spoken replies are prompted to avoid markdown.
        Assert.Contains("spoken aloud", _llm.Requests[^1][0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Relevant_memories_are_injected_into_the_system_prompt()
    {
        _retriever.Results.Add(new ScoredMemory(new MemoryItem { Id = 1, Content = "User prefers C# for software development." }, 0.8f));
        using var session = CreateSession();
        await session.SubmitAsync(new TurnRequest("What language should I use?", InputSource.Text));
        var system = _llm.Requests[^1][0].Content;
        Assert.Contains("User prefers C# for software development.", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replies_are_in_the_assistants_language_whatever_the_user_writes()
    {
        _assistant.Set(PromptBuilderTests.Profile("it"));
        using var session = CreateSession();
        var stored = new List<StoredMessage>();
        session.UserMessageAdded += (_, m) => stored.Add(m);

        await session.SubmitAsync(new TurnRequest("Explique o que é uma variável, por favor.", InputSource.Text));

        Assert.Contains("Always reply in Italian", _llm.Requests[^1][0].Content, StringComparison.Ordinal);
        Assert.Equal("pt", Assert.Single(stored).Language);
    }

    [Fact]
    public void Titles_are_trimmed_to_first_line()
    {
        Assert.Equal("Hello", AssistantSession.MakeTitle("Hello\nsecond line"));
        Assert.EndsWith("...", AssistantSession.MakeTitle(new string('a', 100)));
    }
}

public sealed class PromptBuilderTests
{
    private static StoredMessage Msg(ChatRole role, string text) => new() { Role = role, Content = text };

    [Fact]
    public void Respects_history_budget_keeping_newest_messages()
    {
        var builder = new PromptBuilder(TestOptions.Create(o => o.Assistant.HistoryTokenBudget = 100), new LocalAI.Core.Assistants.AssistantContext());
        var history = Enumerable.Range(0, 20)
            .Select(i => Msg(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"message number {i} " + new string('x', 60)))
            .ToList();
        var prompt = builder.Build(history, "latest", [], false, DateTimeOffset.Now);

        Assert.True(prompt.Count < 10);
        Assert.Equal(ChatRole.System, prompt[0].Role);
        Assert.Equal(ChatRole.User, prompt[1].Role); // history always starts with a user turn
        Assert.Contains("message number 19", prompt[^2].Content, StringComparison.Ordinal);
        Assert.Equal("latest", prompt[^1].Content);
    }

    internal static LocalAI.Core.Assistants.AssistantProfile Profile(string language) =>
        new(1, "Diana", "Speaks like a pirate.", "model", "voice", language, VoiceStyle.Default, DateTimeOffset.Now);

    [Fact]
    public void Adds_name_style_and_the_assistants_reply_language()
    {
        var context = new LocalAI.Core.Assistants.AssistantContext();
        context.Set(Profile("it"));
        var system = new PromptBuilder(TestOptions.Create(), context).Build([], "Hello", [], false, DateTimeOffset.Now)[0].Content;
        Assert.Contains("You are Diana", system, StringComparison.Ordinal);
        Assert.Contains("Speaks like a pirate.", system, StringComparison.Ordinal);
        Assert.Contains("Always reply in Italian", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Reminds_the_reply_language_in_the_last_user_turn()
    {
        var context = new LocalAI.Core.Assistants.AssistantContext();
        context.Set(Profile("it"));
        var prompt = new PromptBuilder(TestOptions.Create(), context).Build([], "Hello", [], false, DateTimeOffset.Now);
        Assert.Equal("Hello\n\n(Reply in Italian.)", prompt[^1].Content);
    }

    [Fact]
    public void Without_an_assistant_the_reply_follows_the_users_language()
    {
        var system = new PromptBuilder(TestOptions.Create(), new LocalAI.Core.Assistants.AssistantContext())
            .Build([], "Hello", [], false, DateTimeOffset.Now)[0].Content;
        Assert.Contains("same language as the user's last message", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Merges_consecutive_same_role_messages_to_keep_alternation()
    {
        var builder = new PromptBuilder(TestOptions.Create(), new LocalAI.Core.Assistants.AssistantContext());
        var history = new List<StoredMessage> { Msg(ChatRole.User, "a"), Msg(ChatRole.User, "b"), Msg(ChatRole.Assistant, "c") };
        var prompt = builder.Build(history, "d", [], false, DateTimeOffset.Now);
        var roles = prompt.Skip(1).Select(m => m.Role).ToList();
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], roles);
    }
}

public sealed class MemoryServiceTests
{
    [Theory]
    [InlineData("[\"User prefers C#.\"]", 1)]
    [InlineData("Sure! Here you go:\n```json\n[\"User likes coffee.\", \"User lives in Rome.\"]\n```", 2)]
    [InlineData("[]", 0)]
    [InlineData("I could not find anything.", 0)]
    [InlineData("[not json", 0)]
    public void Parses_facts_robustly(string raw, int expected) =>
        Assert.Equal(expected, MemoryService.ParseFacts(raw).Count);

    [Fact]
    public async Task Extracts_and_deduplicates_memories()
    {
        var llm = new FakeLanguageModel { Responder = _ => ["[\"User prefers C# for software.\"]"] };
        var store = new InMemoryMemoryStore();
        var service = new MemoryService(store, new NoMemoryRetriever(), new FakeEmbeddings(), llm,
            TestOptions.Create(), NullLogger<MemoryService>.Instance);

        var first = await service.ExtractAndStoreAsync(1, "I prefer writing software in C#.", "Noted.", CancellationToken.None);
        var second = await service.ExtractAndStoreAsync(1, "Did I say I like C#?", "Yes.", CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second); // same fact → duplicate by embedding similarity
        Assert.Single(store.Items);
        Assert.NotNull(store.Items[0].Embedding);
        Assert.Equal("fake-embed", store.Items[0].EmbeddingModel);
        Assert.True(llm.Requests[0].Count == 2 && llm.Requests[0][0].Content.Contains("JSON array", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stores_without_vector_when_embeddings_are_unavailable()
    {
        var store = new InMemoryMemoryStore();
        var service = new MemoryService(store, new NoMemoryRetriever(), new FakeEmbeddings { IsAvailable = false }, new FakeLanguageModel(),
            TestOptions.Create(), NullLogger<MemoryService>.Instance);
        var item = await service.RememberAsync("User likes coffee.", null, CancellationToken.None);
        Assert.NotNull(item);
        Assert.Null(store.Items[0].Embedding);
        Assert.Null(await service.RememberAsync("user likes coffee.", null, CancellationToken.None)); // exact duplicate
    }

    [Fact]
    public async Task Disabled_memory_returns_nothing()
    {
        var retriever = new NoMemoryRetriever();
        retriever.Results.Add(new ScoredMemory(new MemoryItem { Content = "x" }, 1));
        var service = new MemoryService(new InMemoryMemoryStore(), retriever, new FakeEmbeddings(), new FakeLanguageModel(),
            TestOptions.Create(o => o.Memory.Enabled = false), NullLogger<MemoryService>.Instance);
        Assert.Empty(await service.RecallAsync("anything", CancellationToken.None));
    }
}
