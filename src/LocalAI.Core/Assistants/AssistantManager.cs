using LocalAI.Configuration;
using LocalAI.Core.Desktop;
using LocalAI.Core.Llm;
using LocalAI.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Assistants;

/// <summary>
/// Creates, switches and deletes assistants. Switching applies the assistant's settings and voice style. Everything
/// heavy follows the AI: turning it on loads the language model, the voice, speech recognition and the embeddings
/// (<see cref="IAiCompanions"/>); turning it off unloads them. An assistant's voice loads only while its AI is on.
/// </summary>
public sealed class AssistantManager(
    IAssistantStore store,
    AssistantContext context,
    ILanguageModel llm,
    ITextToSpeech tts,
    IAiCompanions companions,
    UserSettingsStore settings,
    IOptions<LocalAiOptions> options,
    ILogger<AssistantManager> logger)
{
    public const int MaxNameLength = 40;
    /// <summary>Longest personality (style prompt): about 1,500 tokens, sent in every request's system message.</summary>
    public const int MaxStyleLength = 6000;
    private const string AutoModel = "auto";

    private readonly LocalAiOptions _options = options.Value;

    /// <summary>Raised after an assistant was created, deleted or got a new shortcut.</summary>
    public event EventHandler? AssistantsChanged;


    private readonly Lock _switchGate = new();
    private readonly SemaphoreSlim _voiceGate = new(1, 1);
    /// <summary>One model change at a time: the model to load is read from the options while it loads.</summary>
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private int _switchGeneration;
    private int _voiceLoads;

    /// <summary>True while the active assistant's voice loads (its tab shows a yellow dot).</summary>
    public bool IsLoadingVoice => Volatile.Read(ref _voiceLoads) > 0;

    /// <summary>Raised (on any thread) when <see cref="IsLoadingVoice"/> changes.</summary>
    public event EventHandler? LoadingChanged;

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
        if (style.Length > MaxStyleLength) throw new AssistantValidationException($"The personality can have at most {MaxStyleLength} characters.");
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
    /// Makes <paramref name="profile"/> the active assistant (its settings). With <paramref name="loadModel"/> its AI
    /// is turned on too; without (switching tabs), whatever model is loaded stays as it is and the assistant's AI is on
    /// only if that model is its own (<see cref="IsModelLoaded"/>). Its voice loads only then: without its AI it would
    /// have nothing to say.
    /// </summary>
    public async Task SwitchAsync(AssistantProfile profile, CancellationToken ct = default, bool loadModel = true)
    {
        lock (_switchGate)
        {
            // The switch itself is instant and ordered: quick clicks on several tabs end on the last one.
            ++_switchGeneration;
            _options.Assistant.ActiveId = profile.Id;
            ApplyToOptions(profile);
            context.Set(profile);
        }
        settings.Set("Assistant", "ActiveId", profile.Id);
        logger.LogInformation("Switched to assistant {Id}", profile.Id);

        if (loadModel) await LoadModelAsync(profile, ct).ConfigureAwait(false);
        else if (IsModelLoaded(profile)) await ApplySensesAsync(profile, ct).ConfigureAwait(false);
    }

    /// <summary>The assistant's hearing and speaker, while its AI is on: each loads or unloads as the user chose.</summary>
    private async Task ApplySensesAsync(AssistantProfile profile, CancellationToken ct)
    {
        var hearing = companions.SetHearingAsync(profile.Hearing, ct);
        if (profile.Speaker) await LoadVoiceAsync(profile, ct).ConfigureAwait(false);
        else tts.Release();
        await hearing.ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the assistant's hearing (speech recognition) or speaker (its voice) on or off, and remembers it for the
    /// next time its AI turns on. Applied at once when its AI is on.
    /// </summary>
    public async Task<AssistantProfile> SetSensesAsync(AssistantProfile profile, bool hearing, bool speaker, CancellationToken ct = default)
    {
        var current = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == profile.Id)
                      ?? throw new AssistantValidationException($"{profile.Name} no longer exists.");
        var updated = current with { Hearing = hearing, Speaker = speaker };
        if (current.Hearing != hearing || current.Speaker != speaker)
        {
            await store.SetSensesAsync(profile.Id, hearing, speaker, ct).ConfigureAwait(false);
            if (context.Current?.Id == profile.Id) context.Update(context.Current with { Hearing = hearing, Speaker = speaker });
            logger.LogInformation("Assistant {Id} hearing {Hearing}, speaker {Speaker}", profile.Id, hearing ? "on" : "off", speaker ? "on" : "off");
        }
        if (context.Current?.Id == profile.Id && IsModelLoaded(updated)) await ApplySensesAsync(updated, ct).ConfigureAwait(false);
        SensesChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>Raised (on any thread) after an assistant's hearing or speaker was turned on or off.</summary>
    public event EventHandler? SensesChanged;

    /// <summary>
    /// Loads the voice of the active assistant (up to a second). A newer switch makes it pointless: the voice of the
    /// assistant switched to then loads instead.
    /// </summary>
    private async Task LoadVoiceAsync(AssistantProfile profile, CancellationToken ct)
    {
        var generation = Volatile.Read(ref _switchGeneration);
        if (Interlocked.Increment(ref _voiceLoads) == 1) LoadingChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _voiceGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (generation != Volatile.Read(ref _switchGeneration) || context.Current?.Id != profile.Id) return;
                if (tts.AvailableVoices.Any(v => v.Id == profile.VoiceId))
                {
                    try { await tts.SetVoiceAsync(profile.VoiceId, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Could not load voice {Voice}", profile.VoiceId); }
                }
                else if (tts.Voice != null)
                {
                    // Not downloaded yet: stay silent rather than speak with the previous assistant's voice.
                    tts.Release();
                }
            }
            finally
            {
                _voiceGate.Release();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _voiceLoads) == 0) LoadingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// True when the assistant's AI is on: the loaded, ready language model is the assistant's own (an assistant on
    /// "auto" takes whichever model is loaded). Several assistants with the same model share it.
    /// </summary>
    public bool IsModelLoaded(AssistantProfile? profile) =>
        profile != null && llm.State == LanguageModelState.Ready &&
        (string.IsNullOrEmpty(profile.ModelId) || profile.ModelId == AutoModel || llm.Info?.Id == profile.ModelId) &&
        // Started with the assistant's own context and cache (another assistant on the same model may use others).
        llm.Info?.RequestedContextSize == profile.Behavior.ContextSize && llm.Info?.CompactCache == profile.Behavior.CompactCache;

    /// <summary>Whether the active assistant's AI is on.</summary>
    public bool IsActiveModelLoaded => IsModelLoaded(context.Current);

    /// <summary>
    /// Turns the assistant's AI on: loads its language model (unloading another assistant's; one runs at a time), its
    /// voice meanwhile (on the CPU), then speech recognition and the embeddings once the model is up. They come after
    /// it so the model's GPU memory is reserved first, and not at all when it failed: they need it.
    /// </summary>
    public async Task LoadModelAsync(AssistantProfile profile, CancellationToken ct = default)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The voice loads meanwhile, if the user left the speaker on.
            Task voice;
            if (profile.Speaker) voice = LoadVoiceAsync(profile, ct);
            else
            {
                tts.Release();
                voice = Task.CompletedTask;
            }
            if (!IsModelLoaded(profile))
            {
                ApplyModel(profile);
                await llm.UnloadAsync(ct).ConfigureAwait(false);
                await llm.LoadAsync(ct).ConfigureAwait(false);
                if (llm.State == LanguageModelState.Ready) logger.LogInformation("AI of assistant {Id} turned on", profile.Id);
            }
            if (llm.State == LanguageModelState.Ready) await companions.StartAsync(profile.Hearing, ct).ConfigureAwait(false);
            await voice.ConfigureAwait(false);
            if (llm.State != LanguageModelState.Ready) tts.Release(); // no AI, nothing to say
        }
        finally
        {
            _modelGate.Release();
        }
    }

    /// <summary>
    /// Turns the AI off: unloads the language model (frees the GPU memory), speech recognition, the embeddings and the
    /// voice.
    /// </summary>
    public async Task UnloadModelAsync(CancellationToken ct = default)
    {
        await _modelGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await llm.UnloadAsync(ct).ConfigureAwait(false);
            await companions.StopAsync(ct).ConfigureAwait(false);
            tts.Release();
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

    /// <summary>Changes how the assistant replies; the next reply follows it.</summary>
    public async Task<AssistantProfile> SetBehaviorAsync(AssistantProfile profile, AssistantBehavior behavior, CancellationToken ct = default)
    {
        var current = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == profile.Id)
                      ?? throw new AssistantValidationException($"{profile.Name} no longer exists.");
        if (current.Behavior == behavior) return current;

        // A new context or cache needs the model started again: only when its Brain is on now.
        var restart = (current.Behavior.ContextSize != behavior.ContextSize || current.Behavior.CompactCache != behavior.CompactCache)
                      && context.Current?.Id == profile.Id && IsModelLoaded(current);
        await store.SetBehaviorAsync(profile.Id, behavior, ct).ConfigureAwait(false);
        var updated = current with { Behavior = behavior };
        if (context.Current?.Id == profile.Id) context.Update(context.Current with { Behavior = behavior });
        logger.LogInformation("Assistant {Id} behavior set to {Creativity}, {Length}, thinking {Thinking}, context {Context}, compact cache {Compact}",
            profile.Id, behavior.Creativity, behavior.ReplyLength, behavior.Thinking, behavior.ContextSize, behavior.CompactCache);
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
        if (restart) await LoadModelAsync(context.Current ?? updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Changes the assistant's personality (empty = the default one). The next reply follows it; its identity note is
    /// written again with it (<see cref="AssistantsChanged"/>).
    /// </summary>
    public async Task<AssistantProfile> SetStyleAsync(AssistantProfile profile, string stylePrompt, CancellationToken ct = default)
    {
        var style = stylePrompt.ReplaceLineEndings("\n").Trim();
        if (style.Length > MaxStyleLength) throw new AssistantValidationException($"The personality can have at most {MaxStyleLength} characters.");
        var current = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == profile.Id)
                      ?? throw new AssistantValidationException($"{profile.Name} no longer exists.");
        if (current.StylePrompt == style) return current;

        await store.SetStyleAsync(profile.Id, style, ct).ConfigureAwait(false);
        var updated = current with { StylePrompt = style };
        if (context.Current?.Id == profile.Id) context.Update(context.Current with { StylePrompt = style });
        logger.LogInformation("Assistant {Id} personality changed", profile.Id);
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>
    /// Changes the assistant's language model, started with <paramref name="contextSize"/> tokens of context (one the
    /// new model can use; 0 = its default). When its Brain is on, the new model starts at once; otherwise it starts the
    /// next time the Brain turns on.
    /// </summary>
    public async Task<AssistantProfile> SetModelAsync(AssistantProfile profile, string modelId, int contextSize, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(modelId)) throw new AssistantValidationException("Choose a language model.");
        var current = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == profile.Id)
                      ?? throw new AssistantValidationException($"{profile.Name} no longer exists.");
        var behavior = current.Behavior with { ContextSize = contextSize };
        if (current.ModelId == modelId && current.Behavior == behavior) return current;

        var restart = context.Current?.Id == profile.Id && IsModelLoaded(current);
        await store.SetModelAsync(profile.Id, modelId, behavior, ct).ConfigureAwait(false);
        var updated = current with { ModelId = modelId, Behavior = behavior };
        if (context.Current?.Id == profile.Id) context.Update(context.Current with { ModelId = modelId, Behavior = behavior });
        logger.LogInformation("Assistant {Id} model set to {Model}, context {Context}", profile.Id, modelId, behavior.ContextSize);
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
        if (restart) await LoadModelAsync(context.Current ?? updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Changes the assistant's voice (one of its native language), its style and its picture. The active assistant
    /// speaks with it from the next sentence on; its voice loads at once when its Brain and Speaker are on.
    /// </summary>
    public async Task<AssistantProfile> SetVoiceAsync(AssistantProfile profile, string voiceId, VoiceStyle voiceStyle, string avatar,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(voiceId)) throw new AssistantValidationException("Choose a voice.");
        var current = (await store.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == profile.Id)
                      ?? throw new AssistantValidationException($"{profile.Name} no longer exists.");
        if (current.VoiceId == voiceId && current.VoiceStyle == voiceStyle && current.Avatar == avatar) return current;

        await store.SetVoiceAsync(profile.Id, voiceId, voiceStyle, avatar, ct).ConfigureAwait(false);
        var updated = current with { VoiceId = voiceId, VoiceStyle = voiceStyle, Avatar = avatar };
        logger.LogInformation("Assistant {Id} voice set to {Voice}", profile.Id, voiceId);
        if (context.Current?.Id == profile.Id)
        {
            var active = context.Current with { VoiceId = voiceId, VoiceStyle = voiceStyle, Avatar = avatar };
            lock (_switchGate) ApplyToOptions(active);
            context.Update(active);
            if (active.Speaker && IsModelLoaded(active)) await LoadVoiceAsync(active, ct).ConfigureAwait(false);
        }
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    /// <summary>
    /// Gives a picture to every assistant that has none yet (those created before pictures existed), chosen by
    /// <paramref name="choose"/>.
    /// </summary>
    public async Task AssignMissingAvatarsAsync(Func<AssistantProfile, string> choose, CancellationToken ct = default)
    {
        var changed = false;
        foreach (var profile in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (profile.Avatar.Length > 0) continue;
            var avatar = choose(profile);
            await store.SetAvatarAsync(profile.Id, avatar, ct).ConfigureAwait(false);
            if (context.Current?.Id == profile.Id) context.Update(context.Current with { Avatar = avatar });
            logger.LogInformation("Assistant {Id} picture set to {Avatar}", profile.Id, avatar);
            changed = true;
        }
        if (changed) AssistantsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The model the next load picks. Only set while no other load runs (startup, or under the model gate).</summary>
    private void ApplyModel(AssistantProfile profile)
    {
        _options.Llm.Model = string.IsNullOrEmpty(profile.ModelId) ? AutoModel : profile.ModelId;
        _options.Llm.ContextSize = profile.Behavior.ContextSize;
        _options.Llm.CompactCache = profile.Behavior.CompactCache;
    }

    private void ApplyToOptions(AssistantProfile profile)
    {
        _options.TextToSpeech.Voice = profile.VoiceId;
        tts.Style = profile.VoiceStyle;
    }
}
