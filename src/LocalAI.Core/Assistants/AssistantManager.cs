using LocalAI.Configuration;
using LocalAI.Core.Desktop;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
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
    MemoryService memory,
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

    public Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default) => store.ListAsync(ct);

    /// <summary>Activates the last used assistant (or the first one) before the engines start. Leaves none active if none exist.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var all = await store.ListAsync(ct).ConfigureAwait(false);
        var active = all.FirstOrDefault(a => a.Id == _options.Assistant.ActiveId) ?? all.FirstOrDefault();
        if (active != null) ApplyToOptions(active);
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

        var profile = await store.CreateAsync(assistant with { Name = name, StylePrompt = style }, VoiceStyle.Default, ct).ConfigureAwait(false);
        logger.LogInformation("Created assistant {Id} (model {Model}, voice {Voice})", profile.Id, profile.ModelId, profile.VoiceId);
        AssistantsChanged?.Invoke(this, EventArgs.Empty);
        await SwitchAsync(profile, ct).ConfigureAwait(false);
        return profile;
    }

    public async Task SwitchAsync(AssistantProfile profile, CancellationToken ct = default)
    {
        _options.Assistant.ActiveId = profile.Id;
        settings.Set("Assistant", "ActiveId", profile.Id);
        ApplyToOptions(profile);
        context.Set(profile);
        logger.LogInformation("Switched to assistant {Id}", profile.Id);

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

        if (llm.Info?.Id != profile.ModelId || llm.State != LanguageModelState.Ready)
        {
            await llm.UnloadAsync(ct).ConfigureAwait(false);
            await llm.LoadAsync(ct).ConfigureAwait(false);
        }
        _ = Task.Run(() => memory.BackfillEmbeddingsAsync(CancellationToken.None), CancellationToken.None);
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
                await llm.UnloadAsync(ct).ConfigureAwait(false);
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

    /// <summary>Applies and saves a new voice style for the active assistant.</summary>
    public async Task SaveVoiceStyleAsync(VoiceStyle style, CancellationToken ct = default)
    {
        if (context.Current is not { } current) return;
        tts.Style = style;
        await store.SetVoiceStyleAsync(current.Id, style, ct).ConfigureAwait(false);
        context.Update(current with { VoiceStyle = style });
    }

    private void ApplyToOptions(AssistantProfile profile)
    {
        _options.Llm.Model = string.IsNullOrEmpty(profile.ModelId) ? AutoModel : profile.ModelId;
        _options.TextToSpeech.Voice = profile.VoiceId;
        tts.Style = profile.VoiceStyle;
    }
}
