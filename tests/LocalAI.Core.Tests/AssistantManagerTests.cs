using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Desktop;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Core.Tests;

public sealed class AssistantManagerTests
{
    private readonly string _settingsPath = Path.Combine(TestPaths.New("settings"), "usersettings.json");
    private readonly InMemoryAssistantStore _store = new();
    private readonly AssistantContext _context = new();
    private readonly FakeLanguageModel _llm = new();
    private readonly FakeTts _tts = new();
    private readonly LocalAiOptions _options;
    private readonly AssistantManager _manager;

    public AssistantManagerTests()
    {
        var options = TestOptions.Create();
        _options = options.Value;
        _manager = new AssistantManager(_store, _context, _llm, _tts, new UserSettingsStore(_settingsPath), options,
            NullLogger<AssistantManager>.Instance);
    }

    private static NewAssistant Draft(string name, string style = "") => new(name, style, "model-a", "voice-a", "it");

    [Fact]
    public async Task Load_without_assistants_leaves_none_active()
    {
        await _manager.LoadAsync();
        Assert.Null(_context.Current);
    }

    [Fact]
    public async Task Create_validates_and_activates_the_new_assistant_with_its_model_and_voice()
    {
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("  ")));
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft(new string('x', AssistantManager.MaxNameLength + 1))));
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("Nova") with { ModelId = "" }));
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("Nova") with { VoiceId = "" }));

        var nova = await _manager.CreateAsync(Draft(" Nova ", " Calm and precise. "));
        Assert.Equal("Nova", nova.Name);
        Assert.Equal("Calm and precise.", nova.StylePrompt);
        Assert.Equal("it", nova.Language);
        Assert.Same(nova, _context.Current);
        Assert.Equal("model-a", _options.Llm.Model);
        Assert.Equal("voice-a", _options.TextToSpeech.Voice);
        Assert.Equal(nova.Id, _options.Assistant.ActiveId);
        Assert.Contains($"\"ActiveId\": {nova.Id}", await File.ReadAllTextAsync(_settingsPath), StringComparison.Ordinal);

        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("nova")));
    }

    [Fact]
    public async Task Quick_switches_end_on_the_last_assistant_and_report_the_voice_loading()
    {
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var marco = await _manager.CreateAsync(Draft("Marco"));
        var ava = await _manager.CreateAsync(Draft("Ava"));
        var loading = new List<bool>();
        _manager.LoadingChanged += (_, _) => { lock (loading) loading.Add(_manager.IsLoadingVoice); };

        await Task.WhenAll(
            _manager.SwitchAsync(nova, loadModel: false),
            _manager.SwitchAsync(marco, loadModel: false),
            _manager.SwitchAsync(ava, loadModel: false));

        Assert.Equal(ava.Id, _context.Current?.Id);
        Assert.False(_manager.IsLoadingVoice);
        lock (loading)
        {
            Assert.Contains(true, loading); // the voice was loading (yellow dot)
            Assert.False(loading[^1]); // and it is done
        }
    }

    [Fact]
    public async Task Switching_never_loads_a_model_only_turning_the_ai_on_does()
    {
        _llm.LoadsModel = () => _options.Llm.Model;
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var marco = await _manager.CreateAsync(Draft("Marco") with { ModelId = "model-b" });
        Assert.True(_manager.IsModelLoaded(marco));
        Assert.False(_manager.IsModelLoaded(nova));
        var loads = _llm.LoadCount;

        await _manager.SwitchAsync(nova, loadModel: false);
        Assert.Equal(loads, _llm.LoadCount); // nothing loaded
        Assert.False(_manager.IsActiveModelLoaded); // Marco's model is not Nova's

        await _manager.LoadModelAsync(nova);
        Assert.True(_manager.IsActiveModelLoaded);
        Assert.False(_manager.IsModelLoaded(marco)); // one model at a time
        var ava = await _manager.CreateAsync(Draft("Ava")); // same model as Nova: shared, no reload
        Assert.True(_manager.IsModelLoaded(ava) && _manager.IsModelLoaded(nova));
        Assert.Equal(loads + 1, _llm.LoadCount);

        await _manager.UnloadModelAsync();
        Assert.False(_manager.IsActiveModelLoaded);
    }

    [Fact]
    public async Task Turning_ais_on_at_once_loads_each_ones_own_model()
    {
        _llm.LoadsModel = () => _options.Llm.Model;
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var marco = await _manager.CreateAsync(Draft("Marco") with { ModelId = "model-b" });
        _llm.Loaded.Clear();
        _llm.LoadDelay = TimeSpan.FromMilliseconds(50);

        // Nova's AI turns on (its button), and while it loads the user clicks Marco's tab and turns Marco's AI on.
        var novaOn = _manager.LoadModelAsync(nova);
        await _manager.SwitchAsync(marco, loadModel: false);
        var marcoOn = _manager.LoadModelAsync(marco);
        await Task.WhenAll(novaOn, marcoOn);

        Assert.Equal(["model-a", "model-b"], _llm.Loaded);
        Assert.True(_manager.IsModelLoaded(marco));
    }

    [Fact]
    public async Task Turning_an_ai_on_is_announced_even_when_its_model_is_loaded()
    {
        // Turning an AI on stops the memory work of an AI that is turning off (AssistantPower), whose model is still loaded.
        _llm.LoadsModel = () => _options.Llm.Model;
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var requested = new List<string>();
        _manager.ModelRequested += (_, a) => requested.Add(a.Name);

        await _manager.LoadModelAsync(nova);

        Assert.Equal(["Nova"], requested);
    }

    [Fact]
    public async Task The_voice_style_chosen_at_creation_is_kept_and_applied_on_switch()
    {
        var deep = new VoiceStyle { Pitch = -3, Speed = 0.9 };
        var nova = await _manager.CreateAsync(Draft("Nova") with { VoiceStyle = deep });
        Assert.Equal(deep, nova.VoiceStyle);
        Assert.Equal(deep, _tts.Style);

        var marco = await _manager.CreateAsync(Draft("Marco") with { ModelId = "model-b" });
        Assert.Equal(VoiceStyle.Default, _tts.Style);
        Assert.Equal("model-b", _options.Llm.Model);

        await _manager.SwitchAsync((await _manager.ListAsync()).Single(a => a.Id == nova.Id));
        Assert.Equal(deep, _tts.Style);
        Assert.Equal("model-a", _options.Llm.Model);

        await _manager.DeleteAsync(marco);
        Assert.Single(await _manager.ListAsync());
    }

    [Fact]
    public async Task Deleting_the_active_assistant_switches_to_another_or_leaves_none_active()
    {
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var marco = await _manager.CreateAsync(Draft("Marco") with { ModelId = "model-b" });

        await _manager.DeleteAsync(marco);
        Assert.Equal(nova.Id, _context.Current?.Id);
        Assert.Equal(nova.Id, _options.Assistant.ActiveId);
        Assert.Equal("model-a", _options.Llm.Model);

        await _manager.DeleteAsync(nova);
        Assert.Null(_context.Current);
        Assert.Equal(0, _options.Assistant.ActiveId);
        Assert.Empty(await _manager.ListAsync());
    }

    [Fact]
    public async Task Load_restores_the_last_active_assistant()
    {
        await _manager.CreateAsync(Draft("Nova"));
        var marco = await _manager.CreateAsync(Draft("Marco"));
        _context.Set(null);

        await _manager.LoadAsync();
        Assert.Equal(marco.Id, _context.Current?.Id);
    }

    [Fact]
    public async Task Shortcuts_are_saved_per_assistant_and_cannot_be_shared()
    {
        HotkeyGesture.TryParse("Ctrl+Alt+D", out var ctrlAltD);
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var marco = await _manager.CreateAsync(Draft("Marco"));
        var changes = 0;
        _manager.AssistantsChanged += (_, _) => changes++;

        var updated = await _manager.SetHotkeyAsync(nova, ctrlAltD);
        Assert.Equal(ctrlAltD, updated.Hotkey);
        Assert.Equal(ctrlAltD, (await _manager.ListAsync()).Single(a => a.Id == nova.Id).Hotkey);
        Assert.Equal(1, changes);

        var error = await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.SetHotkeyAsync(marco, ctrlAltD));
        Assert.Contains("Nova", error.Message, StringComparison.Ordinal);

        await _manager.SetHotkeyAsync(nova, ctrlAltD); // unchanged: no notification
        Assert.Equal(1, changes);

        await _manager.SetHotkeyAsync(nova, null);
        Assert.Null((await _manager.ListAsync()).Single(a => a.Id == nova.Id).Hotkey);
        await _manager.SetHotkeyAsync(marco, ctrlAltD);
        Assert.Equal(ctrlAltD, _context.Current?.Hotkey); // Marco is the active assistant
        Assert.Equal(3, changes);
    }
}
