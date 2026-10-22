using System.Text.Json;
using LocalAI.Core.Assistant;
using LocalAI.Core.Conversations;
using LocalAI.Core.Language;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
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
    private readonly FakeTts _tts = new();
    private readonly FakePlayer _player = new();
    private MemoryHarness _memory = null!;

    private AssistantSession CreateSession(Action<Configuration.LocalAiOptions>? configure = null)
    {
        var options = TestOptions.Create(o => configure?.Invoke(o));
        _memory = new MemoryHarness(_llm, _store, _assistant, options: options);
        var memory = _memory.Vault;
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
        Assert.Equal("What is my name?\n\n(Reply in English.)", prompt[^1].Content);
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
        Assert.Equal("And more formally?\n\n(Reply in English.)", prompt[^1].Content);
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
    public async Task The_profile_and_relevant_notes_are_injected_into_the_system_prompt()
    {
        using var session = CreateSession();
        var nova = await _memory.Assistants.CreateAsync(new LocalAI.Core.Assistants.NewAssistant("Nova", "", "m", "v", "en"), VoiceStyle.Default);
        _assistant.Set(nova);
        _memory.Storage.Write("nova/nova-profile.md",
            "---\ntype: profile\n---\n# Nova - Profile\n\n<!-- local-ai:begin -->\n## Preferences\n- User prefers C# for software development.\n<!-- local-ai:end -->\n");
        _memory.Storage.Write("nova/conversations/2026-10-01-rest-api.md",
            "---\ntype: conversation\nconversation: 9\n---\n# REST API\n\n<!-- local-ai:begin -->\n## Summary\nThe user designed the orders REST API with pagination.\n<!-- local-ai:end -->\n");
        await _memory.Vault.ScanAsync(nova.Id, CancellationToken.None);

        await session.SubmitAsync(new TurnRequest("How did we paginate the orders API?", InputSource.Text));

        var system = _llm.Requests[^1][0].Content;
        Assert.Contains("User prefers C# for software development.", system, StringComparison.Ordinal);
        Assert.Contains("[[2026-10-01-rest-api]]", system, StringComparison.Ordinal);
        Assert.Contains("orders REST API with pagination", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replies_follow_the_language_the_user_writes_or_speaks_not_the_voice()
    {
        _assistant.Set(PromptBuilderTests.Profile("it")); // an Italian voice
        using var session = CreateSession();
        var stored = new List<StoredMessage>();
        session.UserMessageAdded += (_, m) => stored.Add(m);

        await session.SubmitAsync(new TurnRequest("Explique o que é uma variável, por favor.", InputSource.Text));
        Assert.Equal("pt", Assert.Single(stored).Language);
        Assert.EndsWith("(Reply in Brazilian Portuguese.)", _llm.Requests[^1][^1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Italian", _llm.Requests[^1][0].Content, StringComparison.Ordinal);

        // Speech: Whisper's language decides.
        await session.SubmitAsync(new TurnRequest("What is a variable?", InputSource.Voice, "en"));
        Assert.EndsWith("(Reply in English.)", _llm.Requests[^1][^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_the_assistants_ai_off_nothing_is_answered_by_another_model()
    {
        var options = TestOptions.Create();
        var assistants = new LocalAI.Core.Assistants.AssistantManager(new InMemoryAssistantStore(), _assistant, _llm, _tts,
            new Configuration.UserSettingsStore(Path.Combine(LocalAI.Tests.TestPaths.New("settings"), "usersettings.json")), options,
            NullLogger<LocalAI.Core.Assistants.AssistantManager>.Instance);
        _llm.LoadsModel = () => options.Value.Llm.Model;
        var nova = await assistants.CreateAsync(new LocalAI.Core.Assistants.NewAssistant("Nova", "", "model-a", "v", "en"));
        await assistants.CreateAsync(new LocalAI.Core.Assistants.NewAssistant("Marco", "", "model-b", "v", "en"));
        await assistants.SwitchAsync(nova, loadModel: false); // Marco's model stays loaded
        _memory = new MemoryHarness(_llm, _store, _assistant, options: options);
        using var session = new AssistantSession(_llm, _store, new PromptBuilder(options, _assistant), new HeuristicLanguageDetector(Languages),
            _memory.Vault, new SpeechOutput(_tts, _player, Languages, NullLogger<SpeechOutput>.Instance), options,
            NullLogger<AssistantSession>.Instance, assistants);
        var requests = _llm.Requests.Count;

        var result = await session.SubmitAsync(new TurnRequest("Hello?", InputSource.Text));

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal(AssistantSession.AiOffMessage, result.Error);
        Assert.Equal(requests, _llm.Requests.Count);
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
        var prompt = builder.Build(history, "latest", MemoryContext.Empty, false, DateTimeOffset.Now);

        Assert.True(prompt.Count < 10);
        Assert.Equal(ChatRole.System, prompt[0].Role);
        Assert.Equal(ChatRole.User, prompt[1].Role); // history always starts with a user turn
        Assert.Contains("message number 19", prompt[^2].Content, StringComparison.Ordinal);
        Assert.Equal("latest", prompt[^1].Content);
    }

    internal static LocalAI.Core.Assistants.AssistantProfile Profile(string language) =>
        new(1, "Nova", "Speaks like a pirate.", "model", "voice", language, VoiceStyle.Default, DateTimeOffset.Now);

    [Fact]
    public void Adds_name_style_and_the_rule_to_reply_in_the_users_language()
    {
        var context = new LocalAI.Core.Assistants.AssistantContext();
        context.Set(Profile("it"));
        var system = new PromptBuilder(TestOptions.Create(), context).Build([], "Hello", MemoryContext.Empty, false, DateTimeOffset.Now)[0].Content;
        Assert.Contains("You are Nova", system, StringComparison.Ordinal);
        Assert.Contains("Speaks like a pirate.", system, StringComparison.Ordinal);
        Assert.Contains("same language as the user's last message", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Italian", system, StringComparison.Ordinal);
    }

    [Fact]
    public void The_identity_note_takes_the_place_of_the_style()
    {
        var context = new LocalAI.Core.Assistants.AssistantContext();
        context.Set(Profile("it"));
        var memory = new MemoryContext(null, [], "## Personality and style\nTalks like a sea captain.");
        var system = new PromptBuilder(TestOptions.Create(), context).Build([], "Hello", memory, false, DateTimeOffset.Now)[0].Content;
        Assert.Contains("Talks like a sea captain.", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Speaks like a pirate.", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Reminds_the_reply_language_in_the_last_user_turn()
    {
        var context = new LocalAI.Core.Assistants.AssistantContext();
        context.Set(Profile("it"));
        var prompt = new PromptBuilder(TestOptions.Create(), context).Build([], "Olá", MemoryContext.Empty, false, DateTimeOffset.Now, userLanguage: "pt");
        Assert.Equal("Olá\n\n(Reply in Brazilian Portuguese.)", prompt[^1].Content);
        // Unknown language: no reminder, the system prompt's rule applies.
        Assert.Equal("Hello", new PromptBuilder(TestOptions.Create(), context).Build([], "Hello", MemoryContext.Empty, false, DateTimeOffset.Now)[^1].Content);
    }

    [Fact]
    public void Without_an_assistant_the_reply_follows_the_users_language()
    {
        var system = new PromptBuilder(TestOptions.Create(), new LocalAI.Core.Assistants.AssistantContext())
            .Build([], "Hello", MemoryContext.Empty, false, DateTimeOffset.Now)[0].Content;
        Assert.Contains("same language as the user's last message", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Merges_consecutive_same_role_messages_to_keep_alternation()
    {
        var builder = new PromptBuilder(TestOptions.Create(), new LocalAI.Core.Assistants.AssistantContext());
        var history = new List<StoredMessage> { Msg(ChatRole.User, "a"), Msg(ChatRole.User, "b"), Msg(ChatRole.Assistant, "c") };
        var prompt = builder.Build(history, "d", MemoryContext.Empty, false, DateTimeOffset.Now);
        var roles = prompt.Skip(1).Select(m => m.Role).ToList();
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], roles);
    }
}

