using System.Diagnostics;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Language;
using LocalAI.Core.Llm;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

/// <summary>The steps the status bar shows: a reply's and the memory work's.</summary>
public sealed class WorkActivityTests
{
    private const string Summary = "{\"summary\": \"S\", \"decisions\": [], \"openItems\": [], \"facts\": [\"Likes tea.\"]}";

    private static List<string> Record(WorkActivity work)
    {
        var steps = new List<string>();
        work.Changed += (_, _) =>
        {
            lock (steps) steps.Add(work.Current?.Text ?? "(none)");
        };
        return steps;
    }

    private static void AssertInOrder(List<string> steps, params string[] expected)
    {
        var index = 0;
        foreach (var e in expected)
        {
            index = steps.FindIndex(index, s => s.StartsWith(e, StringComparison.Ordinal));
            Assert.True(index >= 0, $"missing \"{e}\" in order: {string.Join(" | ", steps)}");
        }
    }

    [Fact]
    public async Task A_reply_shows_its_steps_then_ends()
    {
        var memory = new MemoryHarness();
        var options = memory.Options;
        var nova = await memory.Assistants.CreateAsync(new NewAssistant("Nova", "", "m", "v", "en"), VoiceStyle.Default);
        memory.Context.Set(nova);
        var languages = Configuration.LanguageData.LoadDefault();
        var speech = new SpeechOutput(new FakeTts(), new FakePlayer(), languages, NullLogger<SpeechOutput>.Instance);
        using var session = new AssistantSession(memory.Llm, memory.Conversations, new PromptBuilder(options, memory.Context),
            new HeuristicLanguageDetector(languages), memory.Vault, speech, options, NullLogger<AssistantSession>.Instance, null, memory.Work);
        var steps = Record(memory.Work);

        await session.SubmitAsync(new TurnRequest("Hello there", InputSource.Text));

        AssertInOrder(steps, "Saving your message", "Memory (RAG): reading the identity", "Building the prompt", "Generating the reply",
            "Saving the reply", "(none)");
        Assert.Null(memory.Work.Current);
    }

    [Fact]
    public async Task Writing_a_conversation_note_shows_each_step()
    {
        var memory = new MemoryHarness(new FakeLanguageModel { Responder = _ => [Summary] });
        var nova = await memory.Assistants.CreateAsync(new NewAssistant("Nova", "", "m", "v", "en"), VoiceStyle.Default);
        memory.Context.Set(nova);
        var c = await memory.Conversations.CreateAsync("Tea");
        await memory.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.User, Content = "I like tea" });
        await memory.Conversations.AddMessageAsync(new StoredMessage { ConversationId = c.Id, Role = ChatRole.Assistant, Content = "Noted" });
        var steps = Record(memory.Work);

        await memory.Vault.WriteConversationNoteAsync(nova.Id, c.Id, CancellationToken.None);

        AssertInOrder(steps, "Conversation note: reading the conversation", "Conversation note: the model summarizes",
            "Conversation note: saving the note", "Index: embedding passage 1 of", "Index: saving");
        Assert.Equal(WorkKind.Memory, memory.Work.Current?.Kind);
    }

    [Fact]
    public async Task Slow_motion_pauses_at_every_step_and_the_reply_comes_first()
    {
        var work = new WorkActivity { SlowMotion = TimeSpan.FromMilliseconds(150) };
        var sw = Stopwatch.StartNew();
        await work.StepAsync(WorkKind.Memory, "Index: checking the notes for changes");
        Assert.True(sw.ElapsedMilliseconds >= 140);

        work.SlowMotion = TimeSpan.Zero;
        await work.StepAsync(WorkKind.Reply, "Generating the reply");
        Assert.Equal("Generating the reply", work.Current?.Text);
        work.End(WorkKind.Reply);
        Assert.Equal("Index: checking the notes for changes", work.Current?.Text); // the memory work is still running
        work.End(WorkKind.Memory);
        Assert.Null(work.Current);
    }
}
