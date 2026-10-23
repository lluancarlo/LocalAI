using LocalAI.Configuration;
using LocalAI.Core.Desktop;
using LocalAI.Core.Llm;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Assistants;

/// <summary>
/// Creates, switches and deletes assistants. Switching applies the assistant's language model, voice and voice style.
/// </summary>
public sealed class AssistantManager(
    IAssistantStore store,
    AssistantContext context,
    ILanguageModel llm,
    ITextToSpeech tts,
    UserSettingsStore settings,
    IOptions<LocalAiOptions> options,
    ILogger<AssistantManager> logger)
{
    public const int MaxNameLength = 40;
    public const int MaxStyleLength = 2000;
    private const string AutoModel = "auto";

    private readonly LocalAiOptions _options = options.Value;

    /// <summary>Raised after an assistant was created, deleted or got a new shortcut.</summary>
    public event EventHandler? AssistantsChanged;

    /// <summary>Raised when an assistant's AI is asked to turn on (the button, a live shortcut), before anything loads.</summary>
    public event EventHandler<AssistantProfile>? ModelRequested;

    private readonly Lock _switchGate = new();
    private readonly SemaphoreSlim _voiceGate = new(1, 1);
    /// <summary>One model change at a time: the model to load is read from the options while it loads.</summary>
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private int _switchGeneration;

    /// <summary>True while the active assistant's voice loads after a switch (its tab shows a yellow dot).</summary>
    public bool IsLoadingVoice { get; private set; }

    /// <summary>Raised (on any thread) when <see cref="IsLoadingVoice"/> changes.</summary>
    public event EventHandler? LoadingChanged;

    private void SetLoadingVoice(bool loading)
    {
        if (IsLoadingVoice == loading) return;
        IsLoadingVoice = loading;
        LoadingChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    /// <summary>Activates the last used assistant (or the first one) before the engines start. Leaves none active if none exist.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var all = await store.ListAsync(ct).ConfigureAwait(false);
        var active = all.FirstOrDefault(a => a.Id == _options.Assistant.ActiveId) ?? all.FirstOrDefault();
        if (active != null)
        {
            ApplyToOptions(active);
            ApplyModel(active);
        }
        context.Set(active);
    }

    public async Task<AssistantProfile> CreateAsync(NewAssistant assistant, CancellationToken ct = default)
    {
        var name = assistant.Name.Trim();
        var style = assistant.StylePrompt.Trim();
        if (name.Length == 0) throw new AssistantValidationException("Choose a name for the assistant.");
        if (name.Length > MaxNameLength) throw new AssistantValidationException($"The name can have at most {MaxNameLength} characters.");
        if (style.Length > MaxStyleLength) throw new AssistantValidationException($"The style can have at most {MaxStyleLength} characters.");
        if (string.IsNullOrEmpty(assistant.ModelId)) throw new AssistantValidationException("Choose a language model.");
        if (string.IsNullOrEmpty(assistant.VoiceId)) throw new AssistantValidationException("Choose a voice.");

        var existing = await store.ListAsync(ct).ConfigureAwait(false);
        if (existing.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new AssistantValidationException($"An assistant named \"{name}\" already exists.");

        var profile = await store.CreateAsync(assistant with { Name = name, StylePrompt = style }, assistant.VoiceStyle ?? VoiceStyle.Default, ct).ConfigureAwait(false);
        logger.LogInformation("Created assistant {Id} (model {Model}, voice {Voice})", profile.Id, profile.ModelId, profile.VoiceId);
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
        await SwitchAsync(profile, ct).ConfigureAwait(false);
        return profile;
    }

    /// <summary>
    /// Makes <paramref name="profile"/> the active assistant (its voice and settings). With <paramref name="loadModel"/>
    /// its language model is loaded too; without (switching tabs), whatever model is loaded stays as it is and the
    /// assistant's AI is on only if that model is its own (<see cref="IsModelLoaded"/>).
    /// </summary>
    public async Task SwitchAsync(AssistantProfile profile, CancellationToken ct = default, bool loadModel = true)
    {
        int generation;
        lock (_switchGate)
        {
            // The switch itself is instant and ordered: quick clicks on several tabs end on the last one.
            generation = ++_switchGeneration;
            _options.Assistant.ActiveId = profile.Id;
            ApplyToOptions(profile);
            context.Set(profile);
            SetLoadingVoice(true);
        }
        settings.Set("Assistant", "ActiveId", profile.Id);
        logger.LogInformation("Switched to assistant {Id}", profile.Id);

        // Loading the voice takes up to a second; a newer switch makes it pointless.
        await _voiceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (generation == Volatile.Read(ref _switchGeneration))
            {
                if (tts.AvailableVoices.Any(v => v.Id == profile.VoiceId))
                {
                    try { await tts.SetVoiceAsync(profile.VoiceId, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not load voice {Voice}", profile.VoiceId); }
                }
                else if (tts.Voice is { } previous)
                {
                    // Not downloaded yet: stay silent rather than speak with the previous assistant's voice.
                    tts.RemoveVoice(previous.Id);
                }
            }
        }
        finally
        {
            _voiceGate.Release();
            if (generation == Volatile.Read(ref _switchGeneration)) SetLoadingVoice(false);
        }

        if (loadModel) await LoadModelAsync(profile, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the assistant's AI is on: the loaded, ready language model is the assistant's own (an assistant on
    /// "auto" takes whichever model is loaded). Several assistants with the same model share it.
    /// </summary>
    public bool IsModelLoaded(AssistantProfile? profile) =>
        profile != null && llm.State == LanguageModelState.Ready &&
        (string.IsNullOrEmpty(profile.ModelId) || profile.ModelId == AutoModel || llm.Info?.Id == profile.ModelId);

    /// <summary>Whether the active assistant's AI is on.</summary>
    public bool IsActiveModelLoaded => IsModelLoaded(context.Current);

    /// <summary>Turns the assistant's AI on: loads its language model (unloading another assistant's; one runs at a time).</summary>
    public async Task LoadModelAsync(AssistantProfile profile, CancellationToken ct = default)
    {
        ModelRequested?.Invoke(this, profile);
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsModelLoaded(profile)) return;
            ApplyModel(profile);
            await llm.UnloadAsync(ct).ConfigureAwait(false);
            await llm.LoadAsync(ct).ConfigureAwait(false);
            logger.LogInformation("AI of assistant {Id} turned on", profile.Id);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>Turns the AI off: unloads the language model (frees the GPU memory).</summary>
    public async Task UnloadModelAsync(CancellationToken ct = default)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await llm.UnloadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _modelGate.Release();
        }
        logger.LogInformation("AI turned off");
    }

    /// <summary>What deleting the assistant would remove.</summary>
    public Task<AssistantDataSummary> GetDataSummaryAsync(AssistantProfile profile, CancellationToken ct = default) =>
        store.GetDataSummaryAsync(profile.Id, ct);

    /// <summary>
    /// Deletes the assistant with its conversations and memories. Deleting the active one switches to another
    /// assistant first, or leaves none active (the app then asks to create one) if it was the last.
    /// </summary>
    public async Task DeleteAsync(AssistantProfile profile, CancellationToken ct = default)
    {
        if (profile.Id == context.Current?.Id)
        {
            var others = (await store.ListAsync(ct).ConfigureAwait(false)).Where(a => a.Id != profile.Id).ToList();
            if (others.Count > 0)
            {
                await SwitchAsync(others[0], ct).ConfigureAwait(false);
            }
            else
            {
                _options.Assistant.ActiveId = 0;
                settings.Set("Assistant", "ActiveId", 0L);
                context.Set(null);
                await UnloadModelAsync(ct).ConfigureAwait(false);
            }
        }
        await store.DeleteAsync(profile.Id, ct).ConfigureAwait(false);
        logger.LogInformation("Deleted assistant {Id}", profile.Id);
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets (or clears, with null) the global shortcut of an assistant. Two assistants cannot share one.</summary>
    public async Task<AssistantProfile> SetHotkeyAsync(AssistantProfile profile, HotkeyGesture? hotkey, CancellationToken ct = default)
    {
        var all = await store.ListAsync(ct).ConfigureAwait(false);
        var current = all.FirstOrDefault(a => a.Id == profile.Id)
                      ?? throw new AssistantValidationException($"{profile.Name} no longer exists.");
        if (hotkey != null && all.FirstOrDefault(a => a.Id != profile.Id && a.Hotkey == hotkey) is { } owner)
            throw new AssistantValidationException($"{hotkey} is already the shortcut of {owner.Name}.");
        if (current.Hotkey == hotkey) return current;

        await store.SetHotkeyAsync(profile.Id, hotkey, ct).ConfigureAwait(false);
        var updated = current with { Hotkey = hotkey };
        if (context.Current?.Id == profile.Id) context.Update(context.Current with { Hotkey = hotkey });
        logger.LogInformation("Assistant {Id} shortcut set to {Hotkey}", profile.Id, hotkey?.ToString() ?? "none");
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>The model the next load picks. Only set while no other load runs (startup, or under the model gate).</summary>
    private void ApplyModel(AssistantProfile profile) =>
        _options.Llm.Model = string.IsNullOrEmpty(profile.ModelId) ? AutoModel : profile.ModelId;

    private void ApplyToOptions(AssistantProfile profile)
    {
        _options.TextToSpeech.Voice = profile.VoiceId;
        tts.Style = profile.VoiceStyle;
    }
}
