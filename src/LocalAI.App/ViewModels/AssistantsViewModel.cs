using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.App.Views;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Audio;
using LocalAI.Core.Desktop;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.Infrastructure;
using LocalAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

/// <summary>An item of the voice list: a speaker of the speech model.</summary>
public sealed record VoiceChoice(VoiceCatalogEntry Voice, string Text);

/// <summary>A file of an assistant's learn folder, as the list shows it.</summary>
public sealed record LearnFileItem(string Name, string Detail);

/// <summary>A language the new assistant can have as its own (one with voices).</summary>
public sealed record LanguageChoice(string Code, string Name);

public sealed record CreativityOption(Creativity Value, string Text);

/// <summary>A context size to choose (0 = the model's default), with what it costs here; one that does not run is disabled.</summary>
public sealed record ContextChoice(int Size, string Text, bool CanChoose)
{
    /// <summary>The choices for a model: its default first, then the sizes it can use, with their cost on this computer.</summary>
    public static IReadOnlyList<ContextChoice> For(LlmCatalogEntry? model, IReadOnlyList<ContextOption> options)
    {
        var list = new List<ContextChoice>
        {
            new(0, model == null ? "Default" : $"Default ({ContextPlanner.Label(model.DefaultContext)})", true),
        };
        foreach (var o in options)
        {
            var cost = o.CacheMb >= 1000 ? $"{o.CacheMb / 1000.0:0.#} GB" : $"{o.CacheMb} MB";
            var fit = o.Fit switch
            {
                ModelFit.Gpu => $"fits your GPU ({o.TotalMb / 1000.0:0.#} GB in all)",
                ModelFit.GpuAndCpu => "spills into the system memory (slower)",
                ModelFit.Cpu => "runs on the CPU (slow)",
                _ => "does not run on this PC",
            };
            list.Add(new ContextChoice(o.Size, $"{ContextPlanner.Label(o.Size)} · context {cost} · {fit}", o.Fit != ModelFit.TooLarge));
        }
        return list;
    }
}

public sealed record ReplyLengthOption(ReplyLength Value, string Text);

/// <summary>The choices of an assistant's behavior, as the forms show them.</summary>
public static class BehaviorOptions
{
    public static IReadOnlyList<CreativityOption> Creativity { get; } =
    [
        new(Core.Assistants.Creativity.Precise, "Precise: sticks to the facts"),
        new(Core.Assistants.Creativity.Balanced, "Balanced"),
        new(Core.Assistants.Creativity.Creative, "Creative: more varied and imaginative"),
    ];

    public static IReadOnlyList<ReplyLengthOption> ReplyLength { get; } =
    [
        new(Core.Assistants.ReplyLength.Short, "Short: a few sentences"),
        new(Core.Assistants.ReplyLength.Normal, "Normal"),
        new(Core.Assistants.ReplyLength.Detailed, "Detailed: thorough answers"),
    ];

    public const string ContextTip =
        "How much the model keeps in view at once: the conversation, your memory and documents, and its reply. More context " +
        "remembers more of a long conversation but takes more GPU memory. Changing it restarts the model.";

    public const string CompactCacheTip =
        "Keeps the context in 8 bits instead of 16: about half the memory, so twice the context fits, with almost the same " +
        "answers. Changing it restarts the model.";

    public const string ThinkingTip =
        "Models that can reason (Qwen3, gpt-oss, DeepSeek and others) think before they answer: better answers to hard " +
        "questions, but slower to start. Other models ignore it. Never used in Live mode.";
}

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
    private readonly MemoryVault _vault;
    private readonly UserSettingsStore _settings;
    private readonly VoiceOptions _voice;
    private readonly AssistantOptions _assistantOptions;
    private readonly ModelBrowser _browser;

    public AssistantsViewModel(AssistantManager manager, AssistantContext context, ModelService models, ModelBrowser browser, ModelsViewModel modelsView,
        ITextToSpeech tts, IAudioPlayer player, LanguageData languages, StartupService startup, AssistantHotkeys hotkeys,
        LiveSelection selection, MemoryVault vault, UserSettingsStore settings, AiCompanions companions, IOptions<LocalAiOptions> options,
        ILogger<AssistantsViewModel> logger)
    {
        _selection = selection;
        _vault = vault;
        _settings = settings;
        _voice = options.Value.Voice;
        _assistantOptions = options.Value.Assistant;
        _loadModelAtStartup = _assistantOptions.LoadModelAtStartup;
        _livePrompt = string.IsNullOrWhiteSpace(_assistantOptions.LivePrompt) ? AssistantOptions.DefaultLivePrompt : _assistantOptions.LivePrompt;
        _readAloudEnabled = _voice.ReadAloudEnabled;
        _liveEnabled = _voice.LiveEnabled;
        _readSelection = _voice.ReadSelection;
        _reactionsIndex = (int)_voice.Reactions;
        _waitForUnfinishedSentences = _voice.WaitForUnfinishedSentences;
        _fillerDelayIndex = NearestFillerDelay(_voice.FillerDelayMs);
        _hotkeys = hotkeys;
        _manager = manager;
        _context = context;
        _models = models;
        _browser = browser;
        _modelsView = modelsView;
        _tts = tts;
        _player = player;
        _languages = languages;
        _startup = startup;
        LanguageChoices = models.Voices.Select(v => v.Language).Distinct()
            .Select(code => new LanguageChoice(code, languages.Languages.TryGetValue(code, out var l) ? l.Name : code))
            .OrderBy(l => l.Name, StringComparer.Ordinal).ToList();
        // The system's language when it has voices, else English.
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        _selectedLanguage = LanguageChoices.FirstOrDefault(l => l.Code == system)
                            ?? LanguageChoices.FirstOrDefault(l => l.Code == "en") ?? LanguageChoices.FirstOrDefault();
        Logger = logger;
        _context.Changed += (_, _) => Dispatcher.UIThread.Post(() => _ = RefreshAsync());
        _models.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshChoices();
            LearnAvailable = _vault.CanLearn && !_models.IsAnyDownloading;
        });
        _hotkeys.ProblemsChanged += (_, _) => Dispatcher.UIThread.Post(ShowHotkeyProblems);
        _vault.NotesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _ = LoadProfileAsync();
            if (!IsLearning) _ = LoadLearnAsync();
        });
        companions.Changed += (_, _) => Dispatcher.UIThread.Post(() => LearnAvailable = _vault.CanLearn && !_models.IsAnyDownloading);
        RefreshChoices();
    }

    internal AssistantManager Manager => _manager;
    internal ILogger Logger { get; }

    /// <summary>Raised when the downloads for a new assistant start (the main window shows their progress).</summary>
    public event EventHandler? DownloadsStarted;

    public ObservableCollection<AssistantItemViewModel> Items { get; } = [];
    public ObservableCollection<VoiceChoice> VoiceChoices { get; } = [];

    /// <summary>The languages with voices; the voice list shows the chosen one's voices.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }

    public IReadOnlyList<CreativityOption> CreativityChoices => BehaviorOptions.Creativity;
    public IReadOnlyList<ReplyLengthOption> ReplyLengthChoices => BehaviorOptions.ReplyLength;
    public string ThinkingTip => BehaviorOptions.ThinkingTip;

    // How the new assistant replies (it can be changed later, in Settings › Assistants).
    [ObservableProperty] private CreativityOption _newCreativity = BehaviorOptions.Creativity[1];
    [ObservableProperty] private ReplyLengthOption _newReplyLength = BehaviorOptions.ReplyLength[1];
    [ObservableProperty] private bool _newThinking;

    private AssistantBehavior NewBehavior => new()
    {
        Creativity = NewCreativity.Value,
        ReplyLength = NewReplyLength.Value,
        Thinking = NewThinking,
        ContextSize = NewContext?.Size ?? 0,
        CompactCache = NewCompactCache,
    };

    private void ResetBehavior()
    {
        NewCreativity = BehaviorOptions.Creativity[1];
        NewReplyLength = BehaviorOptions.ReplyLength[1];
        NewThinking = false;
        NewCompactCache = false;
        NewContext = ContextChoices.FirstOrDefault();
    }

    public string ContextTip => BehaviorOptions.ContextTip;
    public string CompactCacheTip => BehaviorOptions.CompactCacheTip;

    /// <summary>The context sizes the chosen model can use on this computer.</summary>
    public ObservableCollection<ContextChoice> ContextChoices { get; } = [];
    [ObservableProperty] private ContextChoice? _newContext;
    [ObservableProperty] private bool _newCompactCache;

    partial void OnNewCompactCacheChanged(bool value) => RefreshContextChoices();
    partial void OnSelectedModelChanged(ChosenModel? value) => RefreshContextChoices();

    /// <summary>The model's sizes, keeping the one chosen if it still runs, else the default.</summary>
    private void RefreshContextChoices()
    {
        var size = NewContext?.Size ?? 0;
        var model = SelectedModel?.Entry ?? (SelectedModel?.Package is { } package ? _models.LanguageModel(package.Id) : null);
        ContextChoices.Clear();
        var options = model == null ? [] : _models.ContextOptions(model, NewCompactCache, _startup.Gpu?.TotalMemoryMb);
        foreach (var choice in ContextChoice.For(model, options)) ContextChoices.Add(choice);
        NewContext = ContextChoices.FirstOrDefault(c => c.Size == size && c.CanChoose) ?? ContextChoices.FirstOrDefault();
    }

    /// <summary>The context sizes of an existing assistant's model (Settings › Assistants).</summary>
    internal IReadOnlyList<ContextChoice> ContextChoicesFor(AssistantProfile profile, bool compact)
    {
        var model = _models.LanguageModel(profile.ModelId);
        var options = model == null ? [] : _models.ContextOptions(model, compact, _startup.Gpu?.TotalMemoryMb);
        return ContextChoice.For(model, options);
    }

    /// <summary>The new assistant's native language: its voice speaks it.</summary>
    [ObservableProperty] private LanguageChoice? _selectedLanguage;

    partial void OnSelectedLanguageChanged(LanguageChoice? value) => RefreshVoices();
    public int MaxNameLength => AssistantManager.MaxNameLength;
    public int MaxStyleLength => AssistantManager.MaxStyleLength;

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StyleCountText), nameof(StyleWarning))] private string _newStyle = "";
    /// <summary>The new assistant's language model, chosen in the model dialog (<see cref="CreateModelPicker"/>).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StyleWarning))] private ChosenModel? _selectedModel;

    /// <summary>"1,240 / 6,000 · ~360 tokens": the style goes into every request, so its size matters.</summary>
    public string StyleCountText =>
        $"{NewStyle.Length:N0} / {MaxStyleLength:N0} · ~{(NewStyle.Length == 0 ? 0 : LocalAI.Core.Assistant.PromptBuilder.EstimateTokens(NewStyle)):N0} tokens";

    /// <summary>
    /// A style that takes more than a tenth of the chosen model's context leaves noticeably less room for the
    /// conversation: said while the user writes it.
    /// </summary>
    public string? StyleWarning
    {
        get
        {
            if (NewStyle.Length == 0) return null;
            var context = SelectedModel?.Entry?.DefaultContext
                          ?? (SelectedModel?.Package is { } package ? _models.LanguageModel(package.Id)?.DefaultContext : null);
            if (context is not { } size) return null;
            var tokens = LocalAI.Core.Assistant.PromptBuilder.EstimateTokens(NewStyle);
            return tokens * 10 > size
                ? $"This model has {size / 1024}K tokens of context: a style this long leaves less room for the conversation. " +
                  "For reference material, use the learn folder instead."
                : null;
        }
    }
    [ObservableProperty] private VoiceChoice? _selectedVoice;
    [ObservableProperty] private string? _previewStatus;
    private bool _modelPickedByUser;
    [ObservableProperty] private string? _error;

    /// <summary>The assistant waiting for the user to confirm its deletion (shown as a modal popup), if any.</summary>
    [ObservableProperty] private DeleteAssistantViewModel? _pendingDelete;

    public async Task RefreshAsync()
    {
        RefreshChoices();
        var all = await _manager.ListAsync();
        CancelHotkeyRecording();
        // Read the selection first: clearing the list makes the combo box set it to null.
        var selectedId = SelectedAssistant?.Profile.Id ?? _context.Current?.Id;
        Items.Clear();
        foreach (var profile in all)
        {
            var model = _models.Find(profile.ModelId)?.DisplayName ?? "Best installed model";
            var voice = _models.FindVoice(profile.VoiceId)?.Label ?? profile.VoiceId;
            Items.Add(new AssistantItemViewModel(this, profile, profile.Id == _context.Current?.Id, $"{model} · {voice}")
            {
                LiveEnabled = LiveEnabled,
            });
        }
        SelectedAssistant = Items.FirstOrDefault(i => i.Profile.Id == selectedId) ?? Items.FirstOrDefault(i => i.IsActive) ?? Items.FirstOrDefault();
        ShowHotkeyProblems();
    }

    // ---------------- Selected assistant ----------------

    /// <summary>The assistant shown under the combo box (its folder, shortcut, pre-prompt and what it remembers).</summary>
    [ObservableProperty] private AssistantItemViewModel? _selectedAssistant;

    /// <summary>The selected assistant's profile note: what it knows about the user, or null before the first one.</summary>
    [ObservableProperty] private string? _profile;

    partial void OnSelectedAssistantChanged(AssistantItemViewModel? value)
    {
        _ = LoadProfileAsync();
        _ = LoadLearnAsync();
    }

    // ---------------- What the selected assistant learned (its learn folder) ----------------

    public ObservableCollection<LearnFileItem> LearnFiles { get; } = [];
    [ObservableProperty] private string _learnStatus = "";
    /// <summary>Files were added, changed or removed since the last Learn.</summary>
    [ObservableProperty] private bool _learnChanged;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanLearn), nameof(LearnTip))] private bool _isLearning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanLearn), nameof(LearnTip))] private bool _learnAvailable;
    [ObservableProperty] private string? _learnProgress;
    [ObservableProperty] private string? _learnError;
    /// <summary>"x.md already exists: replace it?", while Add files waits for the answer.</summary>
    [ObservableProperty] private string? _replaceQuestion;
    private TaskCompletionSource<bool>? _replaceAnswer;

    public bool CanLearn => LearnAvailable && !IsLearning;
    public string LearnTip => IsLearning ? "Learning…" : _models.IsAnyDownloading ? "Wait for the downloads to finish." : LearnAvailable
        ? "Learns every file of the folder again from scratch, replacing what was learned before. The chat waits until it is done."
        : "Turn the AI on to learn.";

    private async Task LoadLearnAsync()
    {
        LearnAvailable = _vault.CanLearn && !_models.IsAnyDownloading;
        if (SelectedAssistant is not { } item)
        {
            LearnFiles.Clear();
            LearnStatus = "";
            LearnChanged = false;
            return;
        }
        try
        {
            var status = await Task.Run(() => _vault.LearnStatusAsync(item.Profile));
            if (SelectedAssistant != item) return; // the user picked another one meanwhile
            LearnFiles.Clear();
            foreach (var f in status.Files)
                LearnFiles.Add(new LearnFileItem(Path.GetFileName(f.Path),
                    $"{FormatBytes(f.Size)} · {f.Modified.ToLocalTime():dd MMM yyyy HH:mm}"));
            LearnStatus = status.LearnedAt is { } at
                ? $"Learned on {at.ToLocalTime():dd MMM yyyy HH:mm}: {status.LearnedFiles} {(status.LearnedFiles == 1 ? "file" : "files")}, {status.Passages} passages."
                : status.Files.Count == 0 ? "Nothing to learn yet: add .md or .txt files." : "Not learned yet: click Learn.";
            LearnChanged = status.Changed && status.LearnedAt != null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not read the learn folder");
            LearnError = $"Could not read the learn folder: {ex.Message}";
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_000_000 ? $"{bytes / 1e6:0.0} MB" : bytes >= 1_000 ? $"{bytes / 1e3:0} KB" : $"{bytes} bytes";

    /// <summary>
    /// Copies the files the user picked into the selected assistant's learn folder. A file with the name of one already
    /// there is copied only if the user says to replace it.
    /// </summary>
    public async Task AddLearnFilesAsync(IReadOnlyList<string> files)
    {
        if (SelectedAssistant is not { } item) return;
        LearnError = null;
        foreach (var file in files)
        {
            try
            {
                var path = await _vault.LearnPathAsync(item.Profile, file);
                if (File.Exists(_vault.FullPath(path)) && !await AskReplaceAsync(Path.GetFileName(file))) continue;
                var text = await File.ReadAllTextAsync(file);
                await _vault.AddLearnFileAsync(item.Profile, file, text);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not add a file to the learn folder");
                LearnError = $"Could not add {Path.GetFileName(file)}: {ex.Message}";
            }
        }
        await LoadLearnAsync();
    }

    private Task<bool> AskReplaceAsync(string name)
    {
        _replaceAnswer = new TaskCompletionSource<bool>();
        ReplaceQuestion = $"{name} is already in the learn folder. Replace it?";
        return _replaceAnswer.Task;
    }

    [RelayCommand]
    private void AnswerReplace(bool replace)
    {
        ReplaceQuestion = null;
        _replaceAnswer?.TrySetResult(replace);
    }

    [RelayCommand]
    private async Task OpenLearnFolderAsync()
    {
        if (SelectedAssistant is not { } item) return;
        try
        {
            var folder = _vault.FullPath(await _vault.LearnFolderAsync(item.Profile));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not open the learn folder");
            LearnError = $"Could not open the folder: {ex.Message}";
        }
    }

    /// <summary>Learns the selected assistant's learn folder again from scratch (all or nothing).</summary>
    [RelayCommand]
    private async Task LearnAsync()
    {
        if (SelectedAssistant is not { } item || !CanLearn) return;
        LearnError = null;
        IsLearning = true;
        LearnProgress = "Reading the files…";
        var progress = new Progress<(int Done, int Total)>(p =>
            LearnProgress = p.Total == 0 ? "Reading the files…" : $"Learning: {p.Done} of {p.Total} passages");
        try
        {
            var passages = await Task.Run(() => _vault.LearnAsync(item.Profile, progress));
            Logger.LogInformation("Learned {Passages} passages", passages);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Learn failed");
            LearnError = $"Could not learn (what was learned before is kept): {ex.Message}";
        }
        finally
        {
            IsLearning = false;
            LearnProgress = null;
            await LoadLearnAsync();
        }
    }

    private async Task LoadProfileAsync()
    {
        if (SelectedAssistant is not { } item)
        {
            Profile = null;
            return;
        }
        try
        {
            var profile = await Task.Run(() => _vault.ReadProfileAsync(item.Profile));
            if (SelectedAssistant == item) Profile = profile; // the user may have picked another one meanwhile
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not read the assistant's profile note");
            if (SelectedAssistant == item) Profile = null;
        }
    }

    // ---------------- Startup ----------------

    /// <summary>Turn the active assistant's AI on when the app starts (off by default: the user turns it on).</summary>
    [ObservableProperty] private bool _loadModelAtStartup;

    partial void OnLoadModelAtStartupChanged(bool value)
    {
        _assistantOptions.LoadModelAtStartup = value;
        _settings.Set("Assistant", "LoadModelAtStartup", value);
    }

    // ---------------- Live replies ----------------

    /// <summary>How to reply in Live mode (the same for every assistant; each one's personality comes from its own pre-prompt).</summary>
    [ObservableProperty] private string _livePrompt = "";

    public int LiveMaxTokens => _assistantOptions.LiveMaxTokens;

    partial void OnLivePromptChanged(string value)
    {
        _assistantOptions.LivePrompt = value;
        _settings.Set("Assistant", "LivePrompt", value);
    }

    [RelayCommand]
    private void ResetLivePrompt() => LivePrompt = AssistantOptions.DefaultLivePrompt;

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

    // ---------------- Live conversation ----------------

    public static IReadOnlyList<string> ReactionLevels { get; } = ["Off", "Subtle", "Frequent"];
    public static IReadOnlyList<string> FillerDelays { get; } = ["Never", "After 1 second", "After 1.5 seconds", "After 2 seconds", "After 3 seconds"];
    private static readonly int[] FillerDelayMs = [0, 1000, 1500, 2000, 3000];

    [ObservableProperty] private int _reactionsIndex;
    [ObservableProperty] private bool _waitForUnfinishedSentences;
    [ObservableProperty] private int _fillerDelayIndex;

    partial void OnReactionsIndexChanged(int value)
    {
        if (value < 0) return;
        _voice.Reactions = (ReactionLevel)value;
        _settings.Set("Voice", "Reactions", _voice.Reactions.ToString());
    }

    partial void OnWaitForUnfinishedSentencesChanged(bool value)
    {
        _voice.WaitForUnfinishedSentences = value;
        _settings.Set("Voice", "WaitForUnfinishedSentences", value);
    }

    partial void OnFillerDelayIndexChanged(int value)
    {
        if (value < 0) return;
        _voice.FillerDelayMs = FillerDelayMs[value];
        _settings.Set("Voice", "FillerDelayMs", _voice.FillerDelayMs);
    }

    /// <summary>The listed delay closest to the configured one.</summary>
    private static int NearestFillerDelay(int ms) =>
        ms <= 0 ? 0 : Array.IndexOf(FillerDelayMs, FillerDelayMs.Skip(1).MinBy(d => Math.Abs(d - ms)));

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
        var vram = _startup.Gpu?.TotalMemoryMb;
        RefreshVoices();

        // Until the user picks a model, follow the recommendation (it changes once the GPU is detected); a picked one
        // that is now in the list (e.g. downloaded meanwhile) shows its new state.
        var picker = CreateModelPicker();
        if (!_modelPickedByUser || SelectedModel == null)
            SelectedModel = _models.RecommendedLlm(vram) is { } recommended ? picker.Chosen(recommended) : null;
        else if (_models.Find(SelectedModel.Id) is { } package)
            SelectedModel = picker.Chosen(package);
    }

    /// <summary>The voices of the chosen native language; the chosen voice stays if it is one of them.</summary>
    private void RefreshVoices()
    {
        // Read the selection first: clearing the list makes the combo box set it to null.
        var voiceId = SelectedVoice?.Voice.Id;
        VoiceChoices.Clear();
        if (_models.VoicePackage is { } voices && SelectedLanguage is { } language)
        {
            foreach (var v in _models.Voices.Where(v => v.Language == language.Code).OrderBy(v => v.Locale, StringComparer.Ordinal))
                VoiceChoices.Add(new VoiceChoice(v, Describe(voices, null, v.Label)));
        }
        SelectedVoice = VoiceChoices.FirstOrDefault(c => c.Voice.Id == voiceId) ?? DefaultVoice();
    }

    /// <summary>The model dialog, on the new assistant's current model.</summary>
    public ModelPickerViewModel CreateModelPicker() =>
        new(_models, _browser, _startup.Gpu?.TotalMemoryMb, SelectedModel?.Package?.Id, Logger);

    /// <summary>The model the user chose in the dialog.</summary>
    public void UseModel(ChosenModel model)
    {
        SelectedModel = model;
        _modelPickedByUser = true;
    }

    private string Describe(ModelPackage package, string? note, string? name = null)
    {
        name ??= package.DisplayName;
        var size = _models.IsInstalled(package) ? "installed" : $"{ModelsViewModel.FormatSize(package.SizeBytes)} download";
        return note == null ? $"{name} · {size}" : $"{name} · {size} · {note}";
    }

    /// <summary>The catalog's default voice of the language shown, else its first one.</summary>
    private VoiceChoice? DefaultVoice() =>
        VoiceChoices.FirstOrDefault(c => c.Voice.Default) ?? VoiceChoices.FirstOrDefault();

    // ---------------- Voice style of the new assistant (fixed once created) ----------------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SpeedText))] private double _speed = VoiceStyle.Default.Speed;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PitchText))] private double _pitch = VoiceStyle.Default.Pitch;

    public string SpeedText => $"{Speed:0.00}×";
    public string PitchText => Math.Abs(Pitch) < 0.001 ? "0" : $"{Pitch:+0.00;-0.00}";

    /// <summary>The settings the chosen voice supports (the others are hidden) and their ranges.</summary>
    [ObservableProperty] private VoiceTuning _voiceTuning = VoiceTuning.Default;

    // A setting the user left at the old voice's value follows the new voice; a changed one is kept, within range.
    partial void OnSelectedVoiceChanged(VoiceChoice? value)
    {
        if (value?.Voice.Tuning is not { } tuning || tuning == VoiceTuning) return;
        var old = VoiceTuning;
        VoiceTuning = tuning;
        Speed = Follow(Speed, old.Speed, tuning.Speed);
        Pitch = Follow(Pitch, old.Pitch, tuning.Pitch);
    }

    internal static double Follow(double value, VoiceSettingRange? old, VoiceSettingRange? range) =>
        range == null ? value
        : old == null || Math.Abs(value - old.Default) < 0.001 ? range.Default
        : range.Clamp(value);

    // Settings the voice does not have keep the engine-neutral value.
    private VoiceStyle NewVoiceStyle => new()
    {
        Speed = VoiceTuning.Speed == null ? VoiceStyle.Default.Speed : Math.Round(Speed, 2),
        Pitch = VoiceTuning.Pitch == null ? VoiceStyle.Default.Pitch : Math.Round(Pitch, 2),
    };

    /// <summary>Back to the values the chosen voice was made for.</summary>
    [RelayCommand]
    private void ResetVoiceStyle()
    {
        Speed = VoiceTuning.Speed?.Default ?? VoiceStyle.Default.Speed;
        Pitch = VoiceTuning.Pitch?.Default ?? VoiceStyle.Default.Pitch;
    }

    /// <summary>Plays the chosen voice with the chosen style (downloads the voice first if needed).</summary>
    [RelayCommand]
    private async Task PreviewVoiceAsync()
    {
        if (SelectedVoice is not { } choice || _models.VoicePackage is not { } voices) return;
        Error = null;
        try
        {
            if (!_models.IsInstalled(voices))
            {
                PreviewStatus = "Downloading the voice…";
                if (!await _modelsView.EnsureInstalledAsync(voices))
                {
                    Error = "The voice could not be downloaded (see Settings › Models).";
                    return;
                }
            }
            PreviewStatus = null;
            var sample = _languages.Languages.TryGetValue(choice.Voice.Language, out var info) ? info.VoiceSample : choice.Voice.DisplayName;
            var style = NewVoiceStyle;
            AudioClip clip;
            try
            {
                clip = await Task.Run(() => _tts.SynthesizeAsync(sample, choice.Voice.Id, style));
            }
            finally
            {
                // A voice loads only for an AI that is on: the engine the preview loaded goes at once (the active
                // voice's stays).
                _tts.ReleaseUnused();
            }
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
            // A model found on Hugging Face joins the list first: creating the assistant turns its Brain on, which needs
            // to know the model (it then waits for the download). It is taken back if the assistant cannot be created.
            var added = model.Package == null ? _models.AddLanguageModel(model.Entry!) : null;
            var package = model.Package ?? added!;
            var draft = new NewAssistant(NewName, NewStyle, package.Id, voice.Voice.Id, SelectedLanguage?.Code ?? voice.Voice.Language, NewVoiceStyle,
                AssistantAvatars.ForGender(voice.Voice.Gender), NewBehavior);
            try
            {
                await Task.Run(() => _manager.CreateAsync(draft));
            }
            catch
            {
                if (added != null) _models.RemoveAddedModel(added);
                throw;
            }
            NewName = "";
            NewStyle = "";
            ResetVoiceStyle();
            ResetBehavior();
            _modelPickedByUser = false;
            IsAddingAssistant = false;
            _ = DownloadMissingAsync([package, .. _models.SharedPackages]);
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

    /// <summary>True while the "Add new assistant" popup is open (the same form as on first launch).</summary>
    [ObservableProperty] private bool _isAddingAssistant;

    [RelayCommand]
    private void AddAssistant()
    {
        Error = null;
        RefreshChoices();
        IsAddingAssistant = true;
    }

    [RelayCommand]
    private void CancelAddAssistant()
    {
        IsAddingAssistant = false;
        Error = null;
        NewName = "";
        NewStyle = "";
        ResetVoiceStyle();
        ResetBehavior();
        _modelPickedByUser = false;
    }

    /// <summary>Opens the confirmation popup with what deleting the assistant would remove.</summary>
    /// <summary>Saves a change to an assistant's behavior; its next reply follows it.</summary>
    internal async Task SaveBehaviorAsync(AssistantItemViewModel item, AssistantBehavior behavior)
    {
        item.Error = null;
        try
        {
            await Task.Run(() => _manager.SetBehaviorAsync(item.Profile, behavior));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not save the assistant's behavior");
            item.Error = $"Could not save: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the assistant's folder of notes (identity, profile, conversations): in data\memory, or in the Obsidian
    /// vault chosen in Settings › Memory.
    /// </summary>
    internal async Task OpenFolderAsync(AssistantItemViewModel item)
    {
        var folder = _vault.FullPath(await _vault.AssistantFolderAsync(item.Profile));
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
    }

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
            await Task.Run(async () =>
            {
                await _vault.DeleteAssistantNotesAsync(pending.Item.Profile); // its folder is found by name: before the assistant goes
                await _manager.DeleteAsync(pending.Item.Profile);
            });
            PendingDelete = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Could not delete assistant");
            pending.Error = $"Could not delete the assistant: {ex.Message}";
        }
    }

    /// <summary>
    /// Continues the downloads that did not finish last time (the app was closed or the network failed), with the rest
    /// of what was asked for after them.
    /// </summary>
    public Task ResumeDownloadsAsync()
    {
        var pending = _models.PendingDownloads();
        if (pending.Count == 0) return Task.CompletedTask;
        Logger.LogInformation("Resuming {Count} unfinished download(s)", pending.Count);
        return DownloadMissingAsync(pending);
    }

    private async Task DownloadMissingAsync(IReadOnlyList<ModelPackage> packages)
    {
        var missing = packages.Where(p => !_models.IsInstalled(p)).ToList();
        if (missing.Count == 0) return;
        // Remembered until installed, so closing the app does not lose them.
        _models.AddPending(missing);
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
    public string PrePrompt => Profile.StylePrompt;

    public IReadOnlyList<CreativityOption> CreativityChoices => BehaviorOptions.Creativity;
    public IReadOnlyList<ReplyLengthOption> ReplyLengthChoices => BehaviorOptions.ReplyLength;
    public string ThinkingTip => BehaviorOptions.ThinkingTip;

    // How the assistant replies: changed here, saved at once.
    [ObservableProperty] private CreativityOption _creativity = BehaviorOptions.Creativity.First(o => o.Value == profile.Behavior.Creativity);
    [ObservableProperty] private ReplyLengthOption _replyLength = BehaviorOptions.ReplyLength.First(o => o.Value == profile.Behavior.ReplyLength);
    [ObservableProperty] private bool _thinking = profile.Behavior.Thinking;

    partial void OnCreativityChanged(CreativityOption value) => SaveBehavior();
    partial void OnReplyLengthChanged(ReplyLengthOption value) => SaveBehavior();
    partial void OnThinkingChanged(bool value) => SaveBehavior();

    public string ContextTip => BehaviorOptions.ContextTip;
    public string CompactCacheTip => BehaviorOptions.CompactCacheTip;

    private IReadOnlyList<ContextChoice>? _contextChoices;
    /// <summary>The context sizes its model can use on this computer (read when first shown).</summary>
    public IReadOnlyList<ContextChoice> ContextChoices => _contextChoices ??= owner.ContextChoicesFor(Profile, CompactCache);

    private ContextChoice? _context;
    public ContextChoice? Context
    {
        get => _context ??= ContextChoices.FirstOrDefault(c => c.Size == Profile.Behavior.ContextSize) ?? ContextChoices.FirstOrDefault();
        set
        {
            if (value == null || value == _context) return;
            _context = value;
            OnPropertyChanged();
            SaveBehavior();
        }
    }

    [ObservableProperty] private bool _compactCache = profile.Behavior.CompactCache;

    partial void OnCompactCacheChanged(bool value)
    {
        // The sizes cost less with the compact cache: shown again, the chosen one kept.
        var size = Context?.Size ?? 0;
        _contextChoices = owner.ContextChoicesFor(Profile, value);
        _context = _contextChoices.FirstOrDefault(c => c.Size == size && c.CanChoose) ?? _contextChoices.FirstOrDefault();
        OnPropertyChanged(nameof(ContextChoices));
        OnPropertyChanged(nameof(Context));
        SaveBehavior();
    }

    private void SaveBehavior()
    {
        // A combo box clears its selection while its list is rebuilt: nothing to save then.
        if (Creativity == null || ReplyLength == null) return;
        _ = owner.SaveBehaviorAsync(this, new AssistantBehavior
        {
            Creativity = Creativity.Value,
            ReplyLength = ReplyLength.Value,
            Thinking = Thinking,
            ContextSize = Context?.Size ?? Profile.Behavior.ContextSize,
            CompactCache = CompactCache,
        });
    }
    public Bitmap Avatar => AvatarImages.Get(Profile.Avatar);
    public bool IsActive { get; } = isActive;
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
    private async Task OpenFolderAsync()
    {
        Error = null;
        try { await owner.OpenFolderAsync(this); }
        catch (Exception ex)
        {
            owner.Logger.LogError(ex, "Could not open the assistant's folder");
            Error = $"Could not open the folder: {ex.Message}";
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
    public string MemoriesText => Count(summary.Memories, "memory note", "memory notes");
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
