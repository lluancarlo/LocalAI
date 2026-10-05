using LocalAI.Configuration;
using LocalAI.Core.Assistants;
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
        var memory = new MemoryService(new InMemoryMemoryStore(), new NoMemoryRetriever(), new FakeEmbeddings(), _llm, options,
            NullLogger<MemoryService>.Instance);
        _manager = new AssistantManager(_store, _context, _llm, _tts, memory, new UserSettingsStore(_settingsPath), options,
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
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("Diana") with { ModelId = "" }));
        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("Diana") with { VoiceId = "" }));

        var diana = await _manager.CreateAsync(Draft(" Diana ", " Calm and precise. "));
        Assert.Equal("Diana", diana.Name);
        Assert.Equal("Calm and precise.", diana.StylePrompt);
        Assert.Equal("it", diana.Language);
        Assert.Same(diana, _context.Current);
        Assert.Equal("model-a", _options.Llm.Model);
        Assert.Equal("voice-a", _options.TextToSpeech.Voice);
        Assert.Equal(diana.Id, _options.Assistant.ActiveId);
        Assert.Contains($"\"ActiveId\": {diana.Id}", await File.ReadAllTextAsync(_settingsPath), StringComparison.Ordinal);

        await Assert.ThrowsAsync<AssistantValidationException>(() => _manager.CreateAsync(Draft("diana")));
    }

    [Fact]
    public async Task Voice_style_is_saved_per_assistant_and_applied_on_switch()
    {
        var diana = await _manager.CreateAsync(Draft("Diana"));
        var deep = new VoiceStyle { Pitch = -3, Speed = 0.9 };
        await _manager.SaveVoiceStyleAsync(deep);
        Assert.Equal(deep, _tts.Style);

        var marco = await _manager.CreateAsync(Draft("Marco") with { ModelId = "model-b" });
        Assert.Equal(VoiceStyle.Default, _tts.Style);
        Assert.Equal("model-b", _options.Llm.Model);

        await _manager.SwitchAsync((await _manager.ListAsync()).Single(a => a.Id == diana.Id));
        Assert.Equal(deep, _tts.Style);
        Assert.Equal("model-a", _options.Llm.Model);

        await _manager.DeleteAsync(marco);
        Assert.Single(await _manager.ListAsync());
    }

    [Fact]
    public async Task Deleting_the_active_assistant_switches_to_another_or_leaves_none_active()
    {
        var diana = await _manager.CreateAsync(Draft("Diana"));
        var marco = await _manager.CreateAsync(Draft("Marco") with { ModelId = "model-b" });

        await _manager.DeleteAsync(marco);
        Assert.Equal(diana.Id, _context.Current?.Id);
        Assert.Equal(diana.Id, _options.Assistant.ActiveId);
        Assert.Equal("model-a", _options.Llm.Model);

        await _manager.DeleteAsync(diana);
        Assert.Null(_context.Current);
        Assert.Equal(0, _options.Assistant.ActiveId);
        Assert.Empty(await _manager.ListAsync());
    }

    [Fact]
    public async Task Load_restores_the_last_active_assistant()
    {
        await _manager.CreateAsync(Draft("Diana"));
        var marco = await _manager.CreateAsync(Draft("Marco"));
        _context.Set(null);

        await _manager.LoadAsync();
        Assert.Equal(marco.Id, _context.Current?.Id);
    }

    private sealed class InMemoryAssistantStore : IAssistantStore
    {
        private readonly List<AssistantProfile> _items = [];

        public Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AssistantProfile>>(_items.ToList());

        public Task<AssistantProfile> CreateAsync(NewAssistant assistant, VoiceStyle voiceStyle, CancellationToken ct = default)
        {
            var profile = new AssistantProfile(_items.Count + 1, assistant.Name, assistant.StylePrompt, assistant.ModelId,
                assistant.VoiceId, assistant.Language, voiceStyle, DateTimeOffset.Now);
            _items.Add(profile);
            return Task.FromResult(profile);
        }

        public Task SetVoiceStyleAsync(long id, VoiceStyle voiceStyle, CancellationToken ct = default)
        {
            var i = _items.FindIndex(a => a.Id == id);
            _items[i] = _items[i] with { VoiceStyle = voiceStyle };
            return Task.CompletedTask;
        }

        public Task<AssistantDataSummary> GetDataSummaryAsync(long id, CancellationToken ct = default) =>
            Task.FromResult(new AssistantDataSummary(0, 0, 0));

        public Task DeleteAsync(long id, CancellationToken ct = default)
        {
            _items.RemoveAll(a => a.Id == id);
            return Task.CompletedTask;
        }
    }
}
