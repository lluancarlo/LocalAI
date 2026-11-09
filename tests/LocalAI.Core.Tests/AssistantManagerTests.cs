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
    private readonly FakeAiCompanions _companions = new();
    private readonly LocalAiOptions _options;
    private readonly AssistantManager _manager;

    public AssistantManagerTests()
    {
        var options = TestOptions.Create();
        _options = options.Value;
        _manager = new AssistantManager(_store, _context, _llm, _tts, _companions, new UserSettingsStore(_settingsPath), options,
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
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("Nova", new string('x', AssistantManager.MaxStyleLength + 1))));
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
        _llm.LoadsModel = () => _options.Llm.Model; // all three share model-a: their AI is on
        var nova = await _manager.CreateAsync(Draft("Nova") with { VoiceId = "voice-n" });
        var marco = await _manager.CreateAsync(Draft("Marco") with { VoiceId = "voice-m" });
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
    public async Task A_new_context_or_cache_restarts_the_brain_with_it()
    {
        _llm.LoadsModel = () => _options.Llm.Model;
        _llm.LoadsContext = () => (_options.Llm.ContextSize, _options.Llm.CompactCache);
        var nova = await _manager.CreateAsync(Draft("Nova") with { Behavior = new AssistantBehavior { ContextSize = 32768 } });
        Assert.Equal((32768, false), (_llm.Info!.RequestedContextSize, _llm.Info.CompactCache));
        var loads = _llm.LoadCount;

        // Only the reply settings change: no restart.
        nova = await _manager.SetBehaviorAsync(nova, nova.Behavior with { Creativity = Creativity.Creative });
        Assert.Equal(loads, _llm.LoadCount);

        // A new context and the compact cache: the model starts again with them, and the AI stays on.
        await _manager.SetBehaviorAsync(nova, nova.Behavior with { ContextSize = 65536, CompactCache = true });
        Assert.Equal(loads + 1, _llm.LoadCount);
        Assert.Equal((65536, true), (_llm.Info!.RequestedContextSize, _llm.Info.CompactCache));
        Assert.True(_manager.IsActiveModelLoaded);
    }

    [Fact]
    public async Task A_new_model_starts_at_once_only_when_the_brain_is_on()
    {
        _llm.LoadsModel = () => _options.Llm.Model;
        _llm.LoadsContext = () => (_options.Llm.ContextSize, _options.Llm.CompactCache);
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var changes = 0;
        _manager.AssistantsChanged += (_, _) => changes++;
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.SetModelAsync(nova, "", 0));

        // Brain on: the new model starts with its context, and the AI stays on.
        var loads = _llm.LoadCount;
        nova = await _manager.SetModelAsync(nova, "model-b", 16384);
        Assert.Equal(loads + 1, _llm.LoadCount);
        Assert.Equal("model-b", _llm.Info!.Id);
        Assert.Equal(16384, _llm.Info.RequestedContextSize);
        Assert.Equal("model-b", _context.Current!.ModelId);
        Assert.True(_manager.IsActiveModelLoaded);
        Assert.Equal(1, changes); // the identity note is written again

        // Brain off: saved, nothing starts.
        await _manager.UnloadModelAsync();
        loads = _llm.LoadCount;
        await _manager.SetModelAsync(nova, "model-c", 0);
        Assert.Equal(loads, _llm.LoadCount);
        Assert.Equal("model-c", (await _store.ListAsync()).Single().ModelId);
        await _manager.LoadModelAsync(_context.Current!);
        Assert.Equal("model-c", _llm.Info!.Id);
    }

    [Fact]
    public async Task The_personality_can_change_within_its_limit()
    {
        var nova = await _manager.CreateAsync(Draft("Nova", "Warm."));
        var changes = 0;
        _manager.AssistantsChanged += (_, _) => changes++;
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.SetStyleAsync(nova, new string('x', AssistantManager.MaxStyleLength + 1)));

        await _manager.SetStyleAsync(nova, "  Calm and precise.\r\nShort answers.  ");

        Assert.Equal("Calm and precise.\nShort answers.", (await _store.ListAsync()).Single().StylePrompt);
        Assert.Equal("Calm and precise.\nShort answers.", _context.Current!.StylePrompt);
        Assert.Equal(1, changes); // the identity note is written again
    }

    [Fact]
    public async Task A_new_voice_speaks_at_once_with_its_style_and_picture()
    {
        _llm.LoadsModel = () => _options.Llm.Model;
        var nova = await _manager.CreateAsync(Draft("Nova") with { VoiceId = "voice-n" });
        var style = new VoiceStyle { Speed = 1.2, Pitch = -0.5 };

        await _manager.SetVoiceAsync(nova, "voice-m", style, AssistantAvatars.Male);

        Assert.Equal("voice-m", _tts.Voice?.Id);
        Assert.Equal(style, _tts.Style);
        Assert.Equal("voice-m", _options.TextToSpeech.Voice);
        var saved = (await _store.ListAsync()).Single();
        Assert.Equal(("voice-m", style, AssistantAvatars.Male), (saved.VoiceId, saved.VoiceStyle, saved.Avatar));
        Assert.Equal("voice-m", _context.Current!.VoiceId);
    }

    [Fact]
    public async Task Hearing_and_speaker_load_with_the_brain_as_the_user_left_them()
    {
        _llm.LoadsModel = () => _options.Llm.Model;
        var nova = await _manager.CreateAsync(Draft("Nova") with { VoiceId = "voice-n" }); // creating turns its AI on
        Assert.True(_companions.IsHearing);
        Assert.Equal("voice-n", _tts.Voice?.Id);

        // Turned off while the Brain is on: unloaded at once, and remembered.
        nova = await _manager.SetSensesAsync(nova, hearing: false, speaker: false);
        Assert.False(_companions.IsHearing);
        Assert.Null(_tts.Voice);
        Assert.False(_context.Current!.Hearing);

        // The next time the Brain turns on, they stay off; the embeddings (memory search) still load.
        await _manager.UnloadModelAsync();
        await _manager.LoadModelAsync((await _store.ListAsync()).Single());
        Assert.True(_companions.IsRunning);
        Assert.False(_companions.IsHearing);
        Assert.Null(_tts.Voice);

        // Back on: loaded at once.
        await _manager.SetSensesAsync(_context.Current!, hearing: true, speaker: true);
        Assert.True(_companions.IsHearing);
        Assert.Equal("voice-n", _tts.Voice?.Id);
    }

    [Fact]
    public async Task Everything_heavy_follows_the_ai_and_a_voice_loads_only_while_its_ai_is_on()
    {
        _llm.LoadsModel = () => _options.Llm.Model;
        var nova = await _manager.CreateAsync(Draft("Nova") with { VoiceId = "voice-n" }); // creating turns its AI on
        Assert.True(_companions.IsRunning);
        Assert.Equal("voice-n", _tts.Voice?.Id);

        await _manager.UnloadModelAsync();
        Assert.False(_companions.IsRunning);
        Assert.Null(_tts.Voice); // released with the AI

        // Marco's AI is off: switching to him loads nothing, not even his voice.
        var marco = await _manager.CreateAsync(Draft("Marco") with { ModelId = "model-b", VoiceId = "voice-m" });
        await _manager.UnloadModelAsync();
        await _manager.SwitchAsync(nova, loadModel: false);
        var starts = _companions.Starts;
        var loading = 0;
        _manager.LoadingChanged += (_, _) => loading++;
        await _manager.SwitchAsync(marco, loadModel: false);
        Assert.Null(_tts.Voice);
        Assert.Equal(0, loading); // no "Loading voice"
        Assert.Equal(starts, _companions.Starts);

        // Turning his AI on loads the model, his voice, speech recognition and the embeddings.
        await _manager.LoadModelAsync(marco);
        Assert.True(_manager.IsActiveModelLoaded);
        Assert.Equal("voice-m", _tts.Voice?.Id);
        Assert.True(_companions.IsRunning);
    }

    [Fact]
    public async Task Without_a_language_model_nothing_else_loads()
    {
        var nova = await _manager.CreateAsync(Draft("Nova"));
        _llm.FailLoad = true; // e.g. its model is not downloaded yet
        await _manager.UnloadModelAsync();
        var starts = _companions.Starts;

        await _manager.LoadModelAsync(nova);

        Assert.False(_manager.IsActiveModelLoaded);
        Assert.Equal(starts, _companions.Starts);
        Assert.Null(_tts.Voice);
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

    [Fact]
    public void The_picture_follows_the_gender_of_the_voice()
    {
        Assert.Equal(AssistantAvatars.Female, AssistantAvatars.ForGender("female"));
        Assert.Equal(AssistantAvatars.Male, AssistantAvatars.ForGender("Male"));
        Assert.Equal(AssistantAvatars.Default, AssistantAvatars.ForGender(null));
        Assert.Equal(AssistantAvatars.Default, AssistantAvatars.ForGender("other"));
    }

    [Fact]
    public async Task Assistants_without_a_picture_get_one_and_the_others_keep_theirs()
    {
        var nova = await _manager.CreateAsync(Draft("Nova"));
        var marco = await _manager.CreateAsync(Draft("Marco") with { Avatar = AssistantAvatars.Male });
        await _manager.SwitchAsync(nova, loadModel: false);
        var changes = 0;
        _manager.AssistantsChanged += (_, _) => changes++;

        await _manager.AssignMissingAvatarsAsync(_ => AssistantAvatars.Female);
        var all = await _manager.ListAsync();
        Assert.Equal(AssistantAvatars.Female, all.Single(a => a.Id == nova.Id).Avatar);
        Assert.Equal(AssistantAvatars.Male, all.Single(a => a.Id == marco.Id).Avatar);
        Assert.Equal(AssistantAvatars.Female, _context.Current?.Avatar); // Nova is the active assistant
        Assert.Equal(1, changes);

        await _manager.AssignMissingAvatarsAsync(_ => AssistantAvatars.Default); // nothing missing: no change
        Assert.Equal(1, changes);
    }
}
