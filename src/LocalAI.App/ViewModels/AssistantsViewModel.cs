using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Audio;
using LocalAI.Core.Speech;
using LocalAI.Infrastructure;
using LocalAI.Models;
using Microsoft.Extensions.Logging;

namespace LocalAI.App.ViewModels;

public sealed record ModelChoice(ModelPackage Package, string Text);

/// <summary>
/// Settings › Assistants and the first-launch dialog: create (name, style, language model and voice), switch and
/// delete assistants. Whatever the new assistant needs is downloaded right after it is created.
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

    public AssistantsViewModel(AssistantManager manager, AssistantContext context, ModelService models, ModelsViewModel modelsView,
        ITextToSpeech tts, IAudioPlayer player, LanguageData languages, StartupService startup, ILogger<AssistantsViewModel> logger)
    {
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
        Items.Clear();
        foreach (var profile in all)
        {
            var model = _models.Find(profile.ModelId)?.DisplayName ?? "Best installed model";
            var voice = _models.Find(profile.VoiceId)?.DisplayName ?? profile.VoiceId;
            Items.Add(new AssistantItemViewModel(this, profile, profile.Id == _context.Current?.Id, $"{model} · {voice}"));
        }
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

    [ObservableProperty] private string? _error;

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
