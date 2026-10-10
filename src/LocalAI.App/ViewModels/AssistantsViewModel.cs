using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Audio;
using LocalAI.Core.Desktop;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.Infrastructure;
using LocalAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

public sealed record ModelChoice(ModelPackage Package, string Text);

/// <summary>
/// Settings › Assistants and the first-launch dialog: the ways to talk offered in the chat (Text is always offered;
/// Read aloud and Live can be turned off, Live with its Read selection option), and create (name, style, language model
/// and voice), switch and delete assistants, and record each assistant's global shortcut. Whatever the new assistant
/// needs is downloaded right after it is created.
/// </summary>
public sealed partial class AssistantsViewModel : ObservableObject
{
    private readonly AssistantManager _manager;
    private readonly AssistantContext _context;
    private readonly ModelService _models;
    private readonly ModelsViewModel _modelsView;
    private readonly ITextToSpeech _tts;
    private readonly IAudioPlayer _player;
    private readonly LanguageData _languages;
    private readonly StartupService _startup;
    private readonly AssistantHotkeys _hotkeys;
    private readonly LiveSelection _selection;
    private readonly UserSettingsStore _settings;
    private readonly VoiceOptions _voice;

    public AssistantsViewModel(AssistantManager manager, AssistantContext context, ModelService models, ModelsViewModel modelsView,
        ITextToSpeech tts, IAudioPlayer player, LanguageData languages, StartupService startup, AssistantHotkeys hotkeys,
        LiveSelection selection, UserSettingsStore settings, IOptions<LocalAiOptions> options, ILogger<AssistantsViewModel> logger)
    {
        _selection = selection;
        _settings = settings;
        _voice = options.Value.Voice;
        _readAloudEnabled = _voice.ReadAloudEnabled;
        _liveEnabled = _voice.LiveEnabled;
        _readSelection = _voice.ReadSelection;
        _hotkeys = hotkeys;
        _manager = manager;
        _context = context;
        _models = models;
        _modelsView = modelsView;
        _tts = tts;
        _player = player;
        _languages = languages;
        _startup = startup;
        Logger = logger;
        _context.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        _models.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshChoices);
        _hotkeys.ProblemsChanged += (_, _) => Dispatcher.UIThread.Post(ShowHotkeyProblems);
        RefreshChoices();
    }

    internal AssistantManager Manager => _manager;
    internal ILogger Logger { get; }

    /// <summary>Raised when the downloads for a new assistant start (the main window shows their progress).</summary>
    public event EventHandler? DownloadsStarted;

    public ObservableCollection<AssistantItemViewModel> Items { get; } = [];
    public ObservableCollection<ModelChoice> ModelChoices { get; } = [];
    public ObservableCollection<ModelChoice> VoiceChoices { get; } = [];
    public int MaxNameLength => AssistantManager.MaxNameLength;
    public int MaxStyleLength => AssistantManager.MaxStyleLength;

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newStyle = "";
    [ObservableProperty] private ModelChoice? _selectedModel;
    [ObservableProperty] private ModelChoice? _selectedVoice;
    [ObservableProperty] private string? _previewStatus;
    private bool _modelPickedByUser;
    private bool _refreshingChoices;
    [ObservableProperty] private string? _error;

    /// <summary>The assistant waiting for the user to confirm its deletion (shown as a modal popup), if any.</summary>
    [ObservableProperty] private DeleteAssistantViewModel? _pendingDelete;

    public async Task RefreshAsync()
    {
        RefreshChoices();
        var all = await _manager.ListAsync();
        CancelHotkeyRecording();
        Items.Clear();
        foreach (var profile in all)
        {
            var model = _models.Find(profile.ModelId)?.DisplayName ?? "Best installed model";
            var voice = _models.Find(profile.VoiceId)?.DisplayName ?? profile.VoiceId;
            Items.Add(new AssistantItemViewModel(this, profile, profile.Id == _context.Current?.Id, $"{model} · {voice}")
            {
                LiveEnabled = LiveEnabled,
            });
        }
        ShowHotkeyProblems();
    }

    // ---------------- Ways to talk ----------------

    /// <summary>Raised when the offered ways to talk change (the chat updates its mode selector).</summary>
    public event EventHandler? WaysToTalkChanged;

    [ObservableProperty] private bool _readAloudEnabled;
    [ObservableProperty] private bool _liveEnabled;
    [ObservableProperty] private bool _readSelection;

    /// <summary>Whether this platform can read text selected in other applications.</summary>
    public bool SelectionSupported => _selection.IsSupported;

    partial void OnReadAloudEnabledChanged(bool value)
    {
        _voice.ReadAloudEnabled = value;
        _settings.Set("Voice", "ReadAloudEnabled", value);
        WaysToTalkChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnLiveEnabledChanged(bool value)
    {
        _voice.LiveEnabled = value;
        _settings.Set("Voice", "LiveEnabled", value);
        if (!value) CancelHotkeyRecording();
        foreach (var item in Items) item.LiveEnabled = value;
        // Shortcuts start live mode: register them only while it is offered.
        _ = RunHotkeyTaskAsync(_hotkeys.RefreshAsync);
        WaysToTalkChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnReadSelectionChanged(bool value)
    {
        _voice.ReadSelection = value;
        _settings.Set("Voice", "ReadSelection", value);
        _selection.Refresh(); // starts or stops watching right away if live mode is on
    }

    // ---------------- Global shortcuts ----------------

    /// <summary>The assistant whose new shortcut is being recorded; the main window sends it the next key press.</summary>
    private AssistantItemViewModel? _recording;

    public bool IsRecordingHotkey => _recording != null;

    internal void BeginHotkeyRecording(AssistantItemViewModel item)
    {
        CancelHotkeyRecording();
        _recording = item;
        item.IsRecordingHotkey = true;
        item.Error = null;
        // Release the registered shortcuts so pressing an existing one reaches the window instead of toggling live mode.
        _ = RunHotkeyTaskAsync(_hotkeys.SuspendAsync);
    }

    public void CancelHotkeyRecording()
    {
        if (_recording is not { } item) return;
        _recording = null;
        item.IsRecordingHotkey = false;
        _ = RunHotkeyTaskAsync(_hotkeys.ResumeAsync);
    }

    /// <summary>Saves the recorded combination, or explains why it cannot be a shortcut (recording continues then).</summary>
    public async Task CompleteHotkeyRecordingAsync(HotkeyModifiers modifiers, string key)
    {
        if (_recording is not { } item) return;
        if (!HotkeyGesture.TryCreate(modifiers, key, out var gesture, out var error))
        {
            item.Error = error;
            return;
        }
        CancelHotkeyRecording();
        await SetHotkeyAsync(item, gesture);
    }

    internal async Task SetHotkeyAsync(AssistantItemViewModel item, HotkeyGesture? gesture)
    {
        item.Error = null;
        try
        {
            await Task.Run(() => _manager.SetHotkeyAsync(item.Profile, gesture));
            await RefreshAsync();
        }
        catch (AssistantValidationException ex)
        {
            item.Error = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not save the shortcut");
            item.Error = $"Could not save the shortcut: {ex.Message}";
        }
    }

    private void ShowHotkeyProblems()
    {
        var problems = _hotkeys.Problems;
        foreach (var item in Items) item.HotkeyProblem = problems.GetValueOrDefault(item.Profile.Id);
    }

    // Called directly (not via Task.Run) so suspend/resume flags are set in click order; the last sync applies them.
    private async Task RunHotkeyTaskAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Logger.LogError(ex, "Could not update the global shortcuts"); }
    }

    private void RefreshChoices()
    {
        _refreshingChoices = true;
        // Read the selection first: clearing the lists makes the combo boxes set it to null.
        var modelId = SelectedModel?.Package.Id;
        var voiceId = SelectedVoice?.Package.Id;
        var recommended = _models.RecommendedLlm(_startup.Gpu?.TotalMemoryMb);
        Replace(ModelChoices, _models.Packages.Where(p => p.Kind == ModelKind.Llm), p =>
            Describe(p, p == recommended ? "recommended for your GPU" : null));
        Replace(VoiceChoices, _models.Packages.Where(p => p.Kind == ModelKind.Voice), p => Describe(p, null));

        // Until the user picks a model, follow the recommendation (it changes once the GPU is detected).
        SelectedModel = (_modelPickedByUser ? ModelChoices.FirstOrDefault(c => c.Package.Id == modelId) : null)
                        ?? ModelChoices.FirstOrDefault(c => c.Package == recommended);
        SelectedVoice = VoiceChoices.FirstOrDefault(c => c.Package.Id == voiceId) ?? DefaultVoice();
        _refreshingChoices = false;
    }

    partial void OnSelectedModelChanged(ModelChoice? value)
    {
        if (!_refreshingChoices && value != null) _modelPickedByUser = true;
    }

    private string Describe(ModelPackage package, string? note)
    {
        var size = _models.IsInstalled(package) ? "installed" : $"{ModelsViewModel.FormatSize(package.SizeBytes)} download";
        return note == null ? $"{package.DisplayName} · {size}" : $"{package.DisplayName} · {size} · {note}";
    }

    private static void Replace(ObservableCollection<ModelChoice> target, IEnumerable<ModelPackage> packages, Func<ModelPackage, string> text)
    {
        target.Clear();
        foreach (var p in packages) target.Add(new ModelChoice(p, text(p)));
    }

    /// <summary>The catalog's default voice for the system language, else the English one.</summary>
    private ModelChoice? DefaultVoice()
    {
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return VoiceChoices.FirstOrDefault(c => c.Package.IsDefault && c.Package.Language == language)
               ?? VoiceChoices.FirstOrDefault(c => c.Package.IsDefault && c.Package.Language == "en")
               ?? VoiceChoices.FirstOrDefault();
    }

    [RelayCommand]
    private async Task PreviewVoiceAsync()
    {
        if (SelectedVoice is not { } choice) return;
        Error = null;
        try
        {
            if (!_models.IsInstalled(choice.Package))
            {
                PreviewStatus = "Downloading the voice…";
                if (!await _modelsView.EnsureInstalledAsync(choice.Package))
                {
                    Error = "The voice could not be downloaded (see Settings › Models).";
                    return;
                }
            }
            PreviewStatus = null;
            var sample = _languages.Languages.TryGetValue(choice.Package.Language ?? "", out var info) ? info.VoiceSample : choice.Package.DisplayName;
            var clip = await Task.Run(() => _tts.SynthesizeAsync(sample, choice.Package.Id, VoiceStyle.Default));
            _player.Stop();
            _player.Enqueue(clip);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Voice preview failed");
            Error = $"Could not play the voice: {ex.Message}";
        }
        finally
        {
            PreviewStatus = null;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        Error = null;
        if (SelectedModel is not { } model || SelectedVoice is not { } voice)
        {
            Error = "Choose a language model and a voice.";
            return;
        }
        try
        {
            var draft = new NewAssistant(NewName, NewStyle, model.Package.Id, voice.Package.Id, voice.Package.Language ?? "");
            await Task.Run(() => _manager.CreateAsync(draft));
            NewName = "";
            NewStyle = "";
            _modelPickedByUser = false;
            _ = DownloadMissingAsync([voice.Package, model.Package, .. _models.SharedPackages]);
        }
        catch (AssistantValidationException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not create assistant");
            Error = $"Could not create the assistant: {ex.Message}";
        }
    }

    /// <summary>Opens the confirmation popup with what deleting the assistant would remove.</summary>
    internal async Task RequestDeleteAsync(AssistantItemViewModel item)
    {
        var summary = await Task.Run(() => _manager.GetDataSummaryAsync(item.Profile));
        var others = Items.Where(i => i.Profile.Id != item.Profile.Id).ToList();
        var consequence = !item.IsActive ? null
            : others.Count > 0 ? $"It is the active assistant: Local AI will switch to {others[0].Name}."
            : "It is your only assistant: you will be asked to create a new one.";
        PendingDelete = new DeleteAssistantViewModel(this, item, summary, consequence);
    }

    internal async Task DeleteAsync(DeleteAssistantViewModel pending)
    {
        try
        {
            await Task.Run(() => _manager.DeleteAsync(pending.Item.Profile));
            PendingDelete = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not delete assistant");
            pending.Error = $"Could not delete the assistant: {ex.Message}";
        }
    }

    private async Task DownloadMissingAsync(IReadOnlyList<ModelPackage> packages)
    {
        var missing = packages.Where(p => !_models.IsInstalled(p)).ToList();
        if (missing.Count == 0) return;
        DownloadsStarted?.Invoke(this, EventArgs.Empty);
        foreach (var package in missing)
            if (!await _modelsView.EnsureInstalledAsync(package)) return;
    }
}

public sealed partial class AssistantItemViewModel(AssistantsViewModel owner, AssistantProfile profile, bool isActive, string details)
    : ObservableObject
{
    public AssistantProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Details { get; } = details;
    public string Style => string.IsNullOrEmpty(Profile.StylePrompt) ? "Default personality" : Profile.StylePrompt;
    public bool IsActive { get; } = isActive;
    public bool CanSwitch => !IsActive;
    public bool HasHotkey => Profile.Hotkey != null;

    /// <summary>Shortcuts start live mode, so they can only be set (and only work) while live mode is offered.</summary>
    [ObservableProperty] private bool _liveEnabled = true;
    public string HotkeyText => IsRecordingHotkey ? "Press the new shortcut… (Esc cancels)" : Profile.Hotkey?.ToString() ?? "none";
    public string RecordHotkeyText => IsRecordingHotkey ? "Cancel" : HasHotkey ? "Change…" : "Set…";

    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyText), nameof(RecordHotkeyText))]
    private bool _isRecordingHotkey;

    /// <summary>Why the shortcut does not work right now (e.g. another application already uses it).</summary>
    [ObservableProperty] private string? _hotkeyProblem;

    [RelayCommand]
    private void RecordHotkey()
    {
        if (IsRecordingHotkey) owner.CancelHotkeyRecording();
        else owner.BeginHotkeyRecording(this);
    }

    [RelayCommand]
    private Task ClearHotkeyAsync()
    {
        owner.CancelHotkeyRecording();
        return owner.SetHotkeyAsync(this, null);
    }

    [RelayCommand]
    private async Task SwitchAsync()
    {
        try { await Task.Run(() => owner.Manager.SwitchAsync(Profile)); }
        catch (Exception ex)
        {
            owner.Logger.LogError(ex, "Could not switch assistant");
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        Error = null;
        try { await owner.RequestDeleteAsync(this); }
        catch (Exception ex)
        {
            owner.Logger.LogError(ex, "Could not read assistant data");
            Error = ex.Message;
        }
    }
}

/// <summary>The "Delete assistant?" popup: lists what will be removed and asks for confirmation.</summary>
public sealed partial class DeleteAssistantViewModel(
    AssistantsViewModel owner, AssistantItemViewModel item, AssistantDataSummary summary, string? consequence) : ObservableObject
{
    public AssistantItemViewModel Item { get; } = item;
    public string Title => $"Delete {Item.Name}?";
    public string ConversationsText => Count(summary.Conversations, "conversation", "conversations") +
                                       $" ({Count(summary.Messages, "message", "messages")})";
    public string MemoriesText => Count(summary.Memories, "memory", "memories");
    public string? Consequence { get; } = consequence;
    public bool HasConsequence => Consequence != null;
    public string ConfirmText => IsDeleting ? "Deleting…" : "Delete permanently";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(CancelCommand))]
    private bool _isDeleting;

    [ObservableProperty] private string? _error;

    private bool CanAct => !IsDeleting;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ConfirmAsync()
    {
        Error = null;
        IsDeleting = true;
        try { await owner.DeleteAsync(this); }
        finally { IsDeleting = false; }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Cancel()
    {
        if (owner.PendingDelete == this) owner.PendingDelete = null;
    }

    private static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
}
