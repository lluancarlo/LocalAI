using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Desktop;
using LocalAI.Core.Language;
using LocalAI.Core.Memory;
using LocalAI.Core.Voice;
using LocalAI.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+D", "Ctrl+Alt+D")]
    [InlineData("alt + ctrl + d", "Ctrl+Alt+D")]
    [InlineData("Win+Shift+F12", "Shift+Win+F12")]
    [InlineData("Ctrl+NumPad7", "Ctrl+NumPad7")]
    [InlineData("Ctrl+Alt+0", "Ctrl+Alt+0")]
    [InlineData("Ctrl+Shift+PageUp", "Ctrl+Shift+PageUp")]
    public void Parses_any_modifier_order_and_formats_canonically(string text, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture.ToString());
        Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var again));
        Assert.Equal(gesture, again);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("Shift+D")]          // no Ctrl, Alt or Win
    [InlineData("Ctrl+Space")]       // push-to-talk
    [InlineData("Ctrl+Ctrl+D")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+Enter")]   // unsupported key
    [InlineData("Hyper+D")]
    [InlineData("Ctrl++D")]
    public void Rejects_combinations_that_cannot_be_global_shortcuts(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void Explains_why_a_combination_is_rejected()
    {
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.Shift, "D", out _, out var error));
        Assert.Contains("Ctrl, Alt or Win", error, StringComparison.Ordinal);
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.Ctrl, "Space", out _, out error));
        Assert.Contains("push-to-talk", error, StringComparison.Ordinal);
        Assert.False(HotkeyGesture.TryCreate(HotkeyModifiers.Ctrl, "Tab", out _, out error));
        Assert.Contains("Tab", error, StringComparison.Ordinal);
        Assert.True(HotkeyGesture.TryCreate(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "d", out var gesture, out _));
        Assert.Equal("D", gesture.Key);
    }
}

/// <summary>An <see cref="AssistantManager"/> over in-memory fakes, shared by the shortcut tests.</summary>
internal sealed class AssistantsHarness
{
    public InMemoryAssistantStore Store { get; } = new();
    public AssistantContext Context { get; } = new();
    public FakeLanguageModel Llm { get; } = new();
    public AssistantManager Manager { get; }

    public AssistantsHarness()
    {
        var options = TestOptions.Create();
        var memory = new MemoryService(new InMemoryMemoryStore(), new NoMemoryRetriever(), new FakeEmbeddings(), Llm, options,
            NullLogger<MemoryService>.Instance);
        Manager = new AssistantManager(Store, Context, Llm, new FakeTts(), memory,
            new UserSettingsStore(Path.Combine(TestPaths.New("settings"), "usersettings.json")), options, NullLogger<AssistantManager>.Instance);
    }

    public Task<AssistantProfile> CreateAsync(string name, string? hotkey = null) => CreateAsync(name, hotkey, "model-a");

    public async Task<AssistantProfile> CreateAsync(string name, string? hotkey, string model)
    {
        var profile = await Manager.CreateAsync(new NewAssistant(name, "", model, "voice-a", "en"));
        return hotkey == null ? profile : await Manager.SetHotkeyAsync(profile, Gesture(hotkey));
    }

    public static HotkeyGesture Gesture(string text) =>
        HotkeyGesture.TryParse(text, out var gesture) ? gesture : throw new ArgumentException(text);
}

public sealed class AssistantHotkeysTests
{
    private readonly AssistantsHarness _assistants = new();
    private readonly FakeGlobalHotkeys _system = new();
    private readonly AssistantHotkeys _hotkeys;

    public AssistantHotkeysTests() =>
        _hotkeys = new AssistantHotkeys(_system, _assistants.Manager, NullLogger<AssistantHotkeys>.Instance);

    private static HotkeyGesture G(string text) => AssistantsHarness.Gesture(text);

    [Fact]
    public async Task Registers_nothing_until_started_then_each_assistants_shortcut()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        await _assistants.CreateAsync("Marco");
        Assert.Empty(_system.Registered);

        await _hotkeys.StartAsync();
        Assert.Equal([G("Ctrl+Alt+D")], _system.Registered);

        var pressed = new List<long>();
        _hotkeys.Pressed += (_, id) => pressed.Add(id);
        Assert.True(_system.Press(G("Ctrl+Alt+D")));
        Assert.Equal([diana.Id], pressed);
    }

    [Fact]
    public async Task Follows_shortcut_changes_and_deleted_assistants()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        await _hotkeys.StartAsync();

        diana = await _assistants.Manager.SetHotkeyAsync(diana, G("Ctrl+Alt+F9"));
        await WaitForAsync(() => _system.Registered.SequenceEqual([G("Ctrl+Alt+F9")]));

        var marco = await _assistants.CreateAsync("Marco", "Ctrl+Alt+M");
        await WaitForAsync(() => _system.Registered.Count == 2);

        await _assistants.Manager.DeleteAsync(diana);
        await WaitForAsync(() => _system.Registered.SequenceEqual([G("Ctrl+Alt+M")]));

        await _assistants.Manager.SetHotkeyAsync(marco, null);
        await WaitForAsync(() => _system.Registered.Count == 0);
    }

    [Fact]
    public async Task A_shortcut_used_by_another_application_is_reported_and_the_others_still_work()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        await _assistants.CreateAsync("Marco", "Ctrl+Alt+M");
        _system.Taken.Add(G("Ctrl+Alt+D"));
        var notified = 0;
        _hotkeys.ProblemsChanged += (_, _) => notified++;

        await _hotkeys.StartAsync();
        Assert.Equal([G("Ctrl+Alt+M")], _system.Registered);
        Assert.Contains("already used", _hotkeys.Problems[diana.Id], StringComparison.Ordinal);
        Assert.Equal(1, notified);

        _system.Taken.Clear();
        await _assistants.Manager.SetHotkeyAsync(diana, G("Ctrl+Alt+F9"));
        await WaitForAsync(() => _hotkeys.Problems.Count == 0);
        Assert.Equal(2, notified);
    }

    [Fact]
    public async Task Suspend_releases_every_shortcut_until_resumed()
    {
        await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        await _hotkeys.StartAsync();

        await _hotkeys.SuspendAsync();
        Assert.Empty(_system.Registered);
        await _hotkeys.ResumeAsync();
        Assert.Equal([G("Ctrl+Alt+D")], _system.Registered);

        _hotkeys.Dispose();
        Assert.Empty(_system.Registered);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // Assistant changes re-register shortcuts in the background.
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10); // up to 5 s on a slow CI runner
        Assert.True(condition());
    }
}

public sealed class LiveActivationTests : IDisposable
{
    private static readonly LanguageData Languages = LanguageData.LoadDefault();
    private readonly AssistantsHarness _assistants = new();
    private readonly FakeGlobalHotkeys _system = new();
    private readonly FakeLiveVoice _voice = new();
    private readonly AssistantSession _session;
    private readonly AssistantHotkeys _hotkeys;
    private readonly LiveActivation _live;
    private readonly List<LiveState> _states = [];

    public LiveActivationTests()
    {
        var options = TestOptions.Create(o => o.Memory.AutoExtract = false);
        var memory = new MemoryService(new InMemoryMemoryStore(), new NoMemoryRetriever(), new FakeEmbeddings(), _assistants.Llm, options,
            NullLogger<MemoryService>.Instance);
        var speech = new SpeechOutput(new FakeTts(), new FakePlayer(), Languages, NullLogger<SpeechOutput>.Instance);
        _session = new AssistantSession(_assistants.Llm, new InMemoryConversationStore(), new PromptBuilder(options, _assistants.Context),
            new HeuristicLanguageDetector(Languages), memory, speech, options, NullLogger<AssistantSession>.Instance);
        _hotkeys = new AssistantHotkeys(_system, _assistants.Manager, NullLogger<AssistantHotkeys>.Instance);
        _live = new LiveActivation(_hotkeys, _assistants.Manager, _assistants.Context, _session, _voice, NullLogger<LiveActivation>.Instance);
        _live.StateChanged += (_, s) => { lock (_states) _states.Add(s); };
    }

    public void Dispose()
    {
        _live.Dispose();
        _hotkeys.Dispose();
        _session.Dispose();
    }

    [Fact]
    public async Task Pressing_the_shortcut_starts_a_new_live_conversation_and_pressing_again_stops_it()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        var conversation = await _session.SubmitAsync(new TurnRequest("Hello", InputSource.Text));
        Assert.NotNull(_session.CurrentConversationId);

        await _live.ToggleAsync(diana.Id);
        Assert.True(_voice.IsContinuous);
        Assert.Equal(LiveState.Live, _live.State);
        Assert.Equal(diana.Id, _live.Assistant?.Id);
        Assert.Null(_session.CurrentConversationId); // the next utterance opens a new conversation
        Assert.Equal([LiveState.Starting, LiveState.Live], _states);

        await _live.ToggleAsync(diana.Id);
        Assert.False(_voice.IsContinuous);
        Assert.Equal(LiveState.Off, _live.State);
        Assert.Null(_live.Assistant);
        Assert.Equal(TurnOutcome.Completed, conversation.Outcome);
    }

    [Fact]
    public async Task The_shortcut_of_another_assistant_switches_to_it()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        var marco = await _assistants.CreateAsync("Marco", "Ctrl+Alt+M", model: "model-b");
        await _assistants.Manager.SwitchAsync(diana);
        var loadsBefore = _assistants.Llm.LoadCount;

        await _live.ToggleAsync(marco.Id);
        Assert.Equal(marco.Id, _assistants.Context.Current?.Id);
        Assert.True(_assistants.Llm.LoadCount > loadsBefore); // Marco's model was loaded
        Assert.Equal(LiveState.Live, _live.State);
    }

    [Fact]
    public async Task While_one_assistant_is_live_the_other_shortcuts_are_ignored()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        var marco = await _assistants.CreateAsync("Marco", "Ctrl+Alt+M");
        await _live.ToggleAsync(diana.Id);

        await _live.ToggleAsync(marco.Id);
        Assert.Equal(diana.Id, _assistants.Context.Current?.Id);
        Assert.True(_voice.IsContinuous);
        Assert.Equal(LiveState.Live, _live.State);
    }

    [Fact]
    public async Task Follows_live_mode_turned_on_and_off_from_the_window()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        await _voice.StartContinuousAsync();
        Assert.Equal(LiveState.Live, _live.State);

        // Live from the window counts: the active assistant's shortcut turns it off.
        await _live.ToggleAsync(diana.Id);
        Assert.False(_voice.IsContinuous);
        Assert.Equal(LiveState.Off, _live.State);
    }

    [Fact]
    public async Task Reports_why_listening_could_not_start()
    {
        var diana = await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        _voice.FailToStart = "Microphone unavailable: unplugged";
        string? failure = null;
        _live.Failed += (_, reason) => failure = reason;

        await _live.ToggleAsync(diana.Id);
        Assert.Equal(LiveState.Off, _live.State);
        Assert.Equal("Microphone unavailable: unplugged", failure);
        Assert.Equal([LiveState.Starting, LiveState.Off], _states);
    }

    [Fact]
    public async Task A_registered_shortcut_toggles_live_mode()
    {
        await _assistants.CreateAsync("Diana", "Ctrl+Alt+D");
        await _hotkeys.StartAsync();

        Assert.True(_system.Press(AssistantsHarness.Gesture("Ctrl+Alt+D")));
        for (var i = 0; i < 500 && _live.State != LiveState.Live; i++) await Task.Delay(10);
        Assert.Equal(LiveState.Live, _live.State);
    }
}
