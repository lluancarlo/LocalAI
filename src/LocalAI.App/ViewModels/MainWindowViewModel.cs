using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.App.Views;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.Infrastructure;
using LocalAI.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

/// <summary>
/// Ok (green), Busy (blue: starting, the steps of a reply), Background (yellow: memory work in the background), Warning,
/// Error, Idle (gray: nothing running).
/// </summary>
public enum StatusLevel { Ok, Busy, Warning, Error, Background, Idle }

/// <summary>Colors shared by the status dot and the tray icon.</summary>
public static class StatusColors
{
    /// <summary>Yellow: the app is working in the background (memory notes).</summary>
    public const string Background = "#F2D14B";
}

public enum AppPage { Chat, Settings, Diagnostics }

/// <summary>
/// Main screen state. Thin layer over <see cref="AssistantSession"/> and <see cref="VoiceConversationController"/>:
/// it never touches engines, the database or native libraries directly.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AssistantSession _session;
    private readonly IConversationStore _store;
    private readonly VoiceConversationController _voice;
    private readonly LiveActivation _live;
    private readonly LiveSelection _selection;
    private readonly StartupService _startup;
    private readonly ILanguageModel _llm;
    private readonly LlamaCppEmbeddingService _embeddings;
    private readonly ISpeechToText _stt;
    private readonly ITextToSpeech _tts;
    private readonly MemoryVault _memory;
    private readonly ModelService _models;
    private readonly AssistantContext _assistant;
    private readonly AssistantManager _manager;
    private readonly UserSettingsStore _settings;
    private readonly LocalAiPaths _paths;
    private readonly LocalAiOptions _options;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly WorkActivity _work;
    private readonly MemoryWorker _memoryWorker;
    private MessageViewModel? _streaming;
    private bool _suppressSelection;
    private GenerationStats? _lastStats;
    private CancellationTokenSource? _searchCts;

    public MainWindowViewModel(
        AssistantSession session, IConversationStore store, VoiceConversationController voice, LiveActivation live, AssistantManager manager,
        LiveSelection selection, StartupService startup,
        ILanguageModel llm, LlamaCppEmbeddingService embeddings, ISpeechToText stt, ITextToSpeech tts,
        MemoryVault memory, MemoryWorker memoryWorker, ModelService models, AssistantContext assistant, UserSettingsStore settings, SettingsViewModel settingsViewModel,
        LocalAiPaths paths, IOptions<LocalAiOptions> options, WorkActivity work, AiCompanions companions,
        ILogger<MainWindowViewModel> logger)
    {
        // Speech recognition loads after the AI turns on (and unloads with it): the voice buttons follow.
        companions.Changed += (_, _) => Ui(UpdateStatus);
        _work = work;
        _memoryWorker = memoryWorker;
        _session = session;
        _store = store;
        _voice = voice;
        _live = live;
        _selection = selection;
        _startup = startup;
        _llm = llm;
        _embeddings = embeddings;
        _stt = stt;
        _tts = tts;
        _memory = memory;
        _models = models;
        _assistant = assistant;
        Settings = settingsViewModel;
        _settings = settings;
        _paths = paths;
        _options = options.Value;
        _logger = logger;
        _preferredMode = _options.Voice.ReplyMode;
        _replyMode = _preferredMode == ReplyMode.ReadAloud && ReadAloudOffered ? ReplyMode.ReadAloud : ReplyMode.Text;
        _lastNonLiveMode = _replyMode;

        _session.UserMessageAdded += (_, m) => Ui(() => OnUserMessage(m));
        _session.AssistantMessageStarted += (_, id) => Ui(() => OnAssistantStarted(id));
        _session.AssistantDelta += OnAssistantDelta;
        _session.TurnCompleted += (_, r) => Ui(() => OnTurnCompleted(r));
        _session.ConversationCreated += (_, c) => Ui(() => OnConversationCreated(c));
        _session.BusyChanged += (_, b) => Ui(() => IsBusy = b);
        _session.ConversationChanged += (_, id) => Ui(() => OnSessionConversationChanged(id));
        _live.StateChanged += (_, s) => Ui(() =>
        {
            OnLiveStateChanged(s);
            UpdateAssistantTabs();
        });
        _live.Failed += (_, reason) => Ui(() => BannerText = $"Could not start listening: {reason}");
        _voice.StateChanged += (_, s) => Ui(() => OnVoiceState(s));
        _voice.Warning += (_, w) => Ui(() => BannerText = w);
        _voice.Transcribed += (_, _) => Ui(() => BannerText = null);
        _startup.StatusChanged += (_, s) => Ui(() => OnSubsystem(s));
        _memory.LearningChanged += (_, _) => Ui(OnLearningChanged);
        manager.SensesChanged += (_, _) => Ui(() =>
        {
            UpdateAssistantTabs();
            UpdateStatus();
        });
        _memory.NotesChanged += (_, _) => Ui(() =>
        {
            _ = LoadMemoriesAsync();
            _ = RefreshMemoryMarksAsync();
        });
        memoryWorker.WorkingChanged += (_, working) => Ui(() => OnWorkingChanged(working));
        _work.Changed += (_, _) => Ui(ApplyStatus);
        _voice.InputLevel += (_, db) => Ui(() => OnInputLevel(db));
        _voice.CaptureChanged += (_, open) => Ui(() =>
        {
            IsMicOpen = open;
            if (!open) OnInputLevel(AudioMath.SilenceDb, reset: true);
        });
        Settings.Changed += (_, _) => Ui(UpdateStatus);
        Settings.Assistants.DownloadsStarted += (_, _) => Ui(ShowDownloads);
        Settings.Assistants.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AssistantsViewModel.IsAddingAssistant)) NotifyAssistantPopup();
        };
        Settings.Assistants.WaysToTalkChanged += (_, _) => Ui(OnWaysToTalkChanged);
        _selection.PendingChanged += (_, _) => Ui(() => PendingSelection = _selection.Pending);
        _llm.StateChanged += (_, _) => Ui(OnModelsChanged);
        _models.Changed += (_, _) => Ui(OnModelsChanged);
        _models.DownloadProgressChanged += (_, _) => Ui(UpdateAssistantTabs);
        _assistant.Changed += (_, _) => Ui(OnAssistantChanged);
        _manager = manager;
        _manager.AssistantsChanged += (_, _) => Ui(() => _ = LoadAssistantTabsAsync());
        _manager.LoadingChanged += (_, _) => Ui(UpdateAssistantTabs);
        Ui(() => _ = LoadAssistantTabsAsync());

        UpdateStatus();
    }

    [ObservableProperty] private string _assistantName = "Local AI";
    /// <summary>The active assistant's picture, next to its name on its messages.</summary>
    [ObservableProperty] private Bitmap _assistantAvatar = AvatarImages.Get(null);
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanToggleLive))] private bool _showWelcome;
    [ObservableProperty] private bool _welcomeIntro = true;

    // The assistant popup: on first launch a welcome, then "Create your assistant"; later the same form, opened by
    // "Add new assistant" (Settings, or + in the assistant tabs), with Cancel.
    public bool ShowAssistantPopup => ShowWelcome || Settings.Assistants.IsAddingAssistant;
    public bool ShowWelcomeIntro => ShowWelcome && WelcomeIntro;
    public bool ShowAssistantForm => ShowAssistantPopup && !ShowWelcomeIntro;
    public bool CanCancelAssistantPopup => !ShowWelcome;
    public string AssistantPopupTitle => ShowWelcome ? "Create your assistant" : "Add new assistant";
    public string AssistantPopupText => ShowWelcome
        ? "Give your AI a name and, optionally, describe its personality. You can add more assistants later."
        : "Give the new assistant a name and, optionally, describe its personality.";

    private void NotifyAssistantPopup()
    {
        foreach (var name in new[] { nameof(ShowAssistantPopup), nameof(ShowWelcomeIntro), nameof(ShowAssistantForm),
                     nameof(CanCancelAssistantPopup), nameof(AssistantPopupTitle), nameof(AssistantPopupText) })
            OnPropertyChanged(name);
    }

    partial void OnShowWelcomeChanged(bool value) => NotifyAssistantPopup();
    partial void OnWelcomeIntroChanged(bool value) => NotifyAssistantPopup();
    private bool _startupCompleted;

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];
    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public SettingsViewModel Settings { get; }

    /// <summary>Raised when the view should scroll to the newest message.</summary>
    public event EventHandler? ScrollToEndRequested;

    [ObservableProperty] private ConversationItemViewModel? _selectedConversation;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _inputText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SendButtonText))] [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(UpdateMemoryCommand))] private bool _isBusy;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private StatusLevel _statusLevel = StatusLevel.Busy;
    [ObservableProperty] private string _gpuText = "GPU: …";
    [ObservableProperty] private string _modelText = "Model: loading…";
    [ObservableProperty] private string _voiceText = "Voice: starting…";
    [ObservableProperty] private string _conversationTitle = "New conversation";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContinuous), nameof(IsTextMode), nameof(IsReadAloudMode), nameof(IsLiveMode))]
    private ReplyMode _replyMode;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanToggleLive), nameof(VoiceUsable))] private bool _voiceAvailable;

    /// <summary>Live and Hold to talk can be used: the voice is ready and no assistant is learning.</summary>
    public bool VoiceUsable => VoiceAvailable && !IsLearning;

    partial void OnVoiceAvailableChanged(bool value) => OnPropertyChanged(nameof(LiveUsable));
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChatPage), nameof(IsSettingsPage), nameof(IsDiagnosticsPage))]
    private AppPage _selectedPage = AppPage.Chat;
    [ObservableProperty] private bool _isMicOpen;
    /// <summary>Microphone level mapped to 0..1 over −60..0 dBFS (for the meter bar).</summary>
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private string _micLevelText = "";
    [ObservableProperty] private IBrush _micLevelBrush = LevelOk;

    private static readonly IBrush LevelOk = new SolidColorBrush(Color.Parse("#9ECE6A"));
    private static readonly IBrush LevelHot = new SolidColorBrush(Color.Parse("#E0AF68"));
    private static readonly IBrush LevelClip = new SolidColorBrush(Color.Parse("#F7768E"));
    [ObservableProperty] private string _diagnosticsText = "";
    [ObservableProperty] private string? _bannerText;

    private readonly ReplyMode _preferredMode;
    /// <summary>The mode to return to when live mode started by a shortcut is turned off.</summary>
    private ReplyMode _lastNonLiveMode;
    /// <summary>True while the reply mode follows a shortcut rather than the user's choice (not saved as a preference).</summary>
    private bool _modeFollowsShortcut;

    public bool IsContinuous => ReplyMode == ReplyMode.Live;

    // Ways to talk offered in the mode selector (Settings > Assistants). Text is always offered.
    public bool ReadAloudOffered => _options.Voice.ReadAloudEnabled;
    public bool LiveOffered => _options.Voice.LiveEnabled;

    /// <summary>Text selected in another application, waiting to go with the next message in live mode (Read selection).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingSelection), nameof(PendingSelectionText))]
    private string? _pendingSelection;

    public bool HasPendingSelection => PendingSelection != null;
    public string PendingSelectionText => $"Selection ready ({PendingSelection?.Length ?? 0:N0} characters): sent with your next message";

    public bool IsTextMode
    {
        get => ReplyMode == ReplyMode.Text;
        set { if (value) ReplyMode = ReplyMode.Text; }
    }

    public bool IsReadAloudMode
    {
        get => ReplyMode == ReplyMode.ReadAloud;
        set { if (value) ReplyMode = ReplyMode.ReadAloud; }
    }

    public bool IsLiveMode
    {
        get => ReplyMode == ReplyMode.Live;
        set { if (value) ReplyMode = ReplyMode.Live; }
    }

    public string SendButtonText => IsBusy ? "Stop" : "Send";

    /// <summary>Open pages, shown as tabs. The chat tab (named after the assistant) is always first and cannot be closed.</summary>
    /// <summary>One tab per assistant at the top of the window: a click shows that assistant's chat.</summary>
    public ObservableCollection<AssistantTabViewModel> AssistantTabs { get; } = [];
    public bool IsChatPage => SelectedPage == AppPage.Chat;
    public bool IsSettingsPage => SelectedPage == AppPage.Settings;
    public bool IsDiagnosticsPage => SelectedPage == AppPage.Diagnostics;

    // ---------------- Startup ----------------

    public async Task LoadConversationsAsync()
    {
        if (_assistant.Current == null)
        {
            Conversations.Clear();
            return;
        }
        try
        {
            var list = await _store.ListAsync();
            Conversations.Clear();
            foreach (var c in list) Conversations.Add(new ConversationItemViewModel(c));
            await RefreshMemoryMarksAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load conversations");
            BannerText = "Could not open the conversation database. See logs.";
        }
    }

    public void OnStartupCompleted()
    {
        _startupCompleted = true;
        UpdateStatus();
        if (_preferredMode == ReplyMode.Live && LiveOffered && VoiceAvailable) ReplyMode = ReplyMode.Live;
        if (_models.PendingDownloads().Count > 0) _ = Settings.Assistants.ResumeDownloadsAsync();
        else OfferModelDownload();
    }

    private void OnAssistantChanged()
    {
        Stop();
        AssistantName = _assistant.Current?.Name ?? "Local AI";
        AssistantAvatar = AvatarImages.Get(_assistant.Current?.Avatar);
        UpdateAssistantTabs();
        ShowWelcome = _assistant.Current == null;
        NewConversation();
        _ = LoadConversationsAsync();
        _ = LoadMemoriesAsync();
        OfferModelDownload();
    }

    private void ShowDownloads()
    {
        Settings.SelectedTab = SettingsViewModel.ModelsTab;
        OpenPage(AppPage.Settings);
    }

    private void OfferModelDownload()
    {
        if (!_startupCompleted || ShowWelcome || _models.HasLanguageModel) return;
        Settings.SelectedTab = SettingsViewModel.ModelsTab;
        OpenPage(AppPage.Settings);
    }

    private void OnModelsChanged()
    {
        UpdateStatus();
        UpdateAssistantTabs();
        Settings.Models.Refresh();

    }

    private void OnSubsystem(SubsystemStatus s) => UpdateStatus();

    private void UpdateStatus()
    {
        var gpu = _startup.Gpu;
        GpuText = gpu != null ? $"GPU: {gpu.Name.Replace("NVIDIA GeForce ", "")}" : "GPU: none (CPU)";

        switch (_llm.State)
        {
            case LanguageModelState.Ready when _assistant.Current != null && !_manager.IsActiveModelLoaded:
                // Another assistant's model is loaded (switching tabs never loads one).
                ModelText = $"Model: {_llm.Info!.DisplayName} (another assistant's)";
                SetStatus(StatusLevel.Warning, "AI off");
                break;
            case LanguageModelState.Ready:
                var info = _llm.Info!;
                ModelText = $"Model: {info.DisplayName} · {info.Quantization}";
                if (info.Backend.StartsWith("CPU", StringComparison.Ordinal))
                    SetStatus(StatusLevel.Warning, _llm.LastError ?? "Ready (CPU)");
                else
                    SetStatus(StatusLevel.Ok, "Ready");
                break;
            case LanguageModelState.Loading:
                ModelText = "Model: loading…";
                SetStatus(StatusLevel.Busy, "Loading model…");
                break;
            case LanguageModelState.NotLoaded when _startupCompleted && _models.HasLanguageModel && _assistant.Current != null:
                ModelText = "Model: off";
                SetStatus(StatusLevel.Warning, "AI off");
                break;
            case LanguageModelState.Failed or LanguageModelState.NotLoaded when !_models.HasLanguageModel:
                ModelText = "Model: none";
                SetStatus(StatusLevel.Warning, ModelSelector.NoModelMessage);
                break;
            case LanguageModelState.Failed:
                ModelText = "Model: unavailable";
                SetStatus(StatusLevel.Error, _llm.LastError ?? "Model failed to load");
                break;
            default:
                SetStatus(StatusLevel.Busy, "Starting…");
                break;
        }

        VoiceAvailable = _stt.State == ComponentState.Ready && Settings.Microphones.Count > 0;
        if (_voice.State is VoiceState.Off or VoiceState.Ready or VoiceState.Error) OnVoiceState(_voice.State);
        DiagnosticsText = BuildDiagnostics();
    }

    /// <summary>The AI's state: a warning or an error is shown on the AI line above the conversations, not in the status bar.</summary>
    private void SetStatus(StatusLevel level, string text)
    {
        AiStatusTip = level is StatusLevel.Warning or StatusLevel.Error ? text : null;
        ApplyStatus();
    }

    /// <summary>The last step of the memory work, kept on screen while the work continues between two steps.</summary>
    private string? _lastMemoryStep;

    /// <summary>
    /// The status bar shows only work: the steps of a reply (blue) and of the memory work in the background (yellow),
    /// else that nothing runs. Whether an assistant's AI is on shows on its tab.
    /// </summary>
    private void ApplyStatus()
    {
        var step = _work.Current;
        if (step is { Kind: WorkKind.Memory }) _lastMemoryStep = step.Text;
        (StatusLevel, StatusText) = step != null ? (step.Kind == WorkKind.Memory ? StatusLevel.Background : StatusLevel.Busy, step.Text)
            : IsBackgroundWorking ? (StatusLevel.Background, _lastMemoryStep ?? "Updating memory")
            : !_startupCompleted ? (StatusLevel.Busy, "Starting…")
            : (StatusLevel.Idle, "Idle");
    }

    /// <summary>Details of the AI's warning or error (hover text of the AI line), or null.</summary>
    [ObservableProperty] private string? _aiStatusTip;

    // ---------------- Watching the work (Diagnostics) ----------------

    /// <summary>Pauses two seconds at every step of the work, so the status bar can be followed.</summary>
    public bool SlowMotion
    {
        get => _work.SlowMotion > TimeSpan.Zero;
        set
        {
            _work.SlowMotion = value ? TimeSpan.FromSeconds(2) : TimeSpan.Zero;
            OnPropertyChanged();
        }
    }

    /// <summary>True while memory work runs in the background (stays on briefly between jobs, so it does not flicker).</summary>
    [ObservableProperty] private bool _isBackgroundWorking;

    partial void OnIsBackgroundWorkingChanged(bool value) => ApplyStatus();

    private DispatcherTimer? _workingOff;

    private void OnWorkingChanged(bool working)
    {
        _workingOff?.Stop();
        if (working)
        {
            IsBackgroundWorking = true;
            return;
        }
        _workingOff ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _workingOff!.Stop();
            IsBackgroundWorking = false;
        });
        _workingOff.Start();
    }

    // ---------------- Text chat ----------------

    /// <summary>
    /// Send (and Stop) only while the assistant's AI is on and it is not learning its documents; otherwise the button
    /// is disabled.
    /// </summary>
    private bool CanSend() => AiOn && !IsLearning && (IsBusy || !string.IsNullOrWhiteSpace(InputText));

    /// <summary>Hover text of the disabled Send button (none while it can send).</summary>
    public string? SendTip => IsLearning ? "Learning its documents: the chat waits until it is done" : AiOn ? null : "Assistant is offline";

    /// <summary>An assistant is learning its documents (Settings › Assistants › Learn): the chat and Live mode wait.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SendTip), nameof(CanToggleLive), nameof(VoiceUsable), nameof(LiveUsable), nameof(AiStatusBrush))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(ToggleHearingCommand), nameof(ToggleSpeakerCommand), nameof(UpdateMemoryCommand))]
    private bool _isLearning;

    private void OnLearningChanged()
    {
        IsLearning = _memory.IsLearning;
        if (IsLearning && IsLiveMode) ReplyMode = _lastNonLiveMode;
        UpdateAssistantTabs();
    }

    // Concurrent execution: while a turn runs, the same button acts as Stop.
    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        if (IsBusy)
        {
            Stop();
            return;
        }
        var text = InputText.Trim();
        if (text.Length == 0 || ShowWelcome) return;
        InputText = "";
        BannerText = null;
        if (IsContinuous)
        {
            // Live mode: the typed message is answered aloud and the conversation keeps listening afterwards.
            await Task.Run(() => _voice.SubmitTextAsync(text, speak: _tts.State == ComponentState.Ready));
            return;
        }
        var speak = ReplyMode == ReplyMode.ReadAloud && _tts.State == ComponentState.Ready;
        await Task.Run(() => _session.SubmitAsync(new TurnRequest(text, InputSource.Text, Speak: speak)));
    }

    [RelayCommand]
    private void Stop()
    {
        _voice.Interrupt();
        _session.CancelCurrentTurn();
    }

    private void OnUserMessage(StoredMessage m)
    {
        if (m.ConversationId != _session.CurrentConversationId) return;
        Messages.Add(MessageViewModel.From(m, AssistantName));
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAssistantStarted(long conversationId)
    {
        if (conversationId != _session.CurrentConversationId) return;
        _streaming = new MessageViewModel(ChatRole.Assistant, "", DateTimeOffset.Now, InputSource.Text, null, AssistantName) { IsStreaming = true };
        Messages.Add(_streaming);
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    // Tokens arrive on a background thread at ~70/s: batch them into one UI update per frame.
    private readonly StringBuilder _pendingDelta = new();
    private readonly StringBuilder _pendingReasoning = new();
    private int _flushScheduled;

    private void OnAssistantDelta(object? sender, AssistantDeltaEventArgs e)
    {
        lock (_pendingDelta) (e.Reasoning ? _pendingReasoning : _pendingDelta).Append(e.Delta);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(FlushDelta, DispatcherPriority.Background);
    }

    private void FlushDelta()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        string text, reasoning;
        lock (_pendingDelta)
        {
            text = _pendingDelta.ToString();
            reasoning = _pendingReasoning.ToString();
            _pendingDelta.Clear();
            _pendingReasoning.Clear();
        }
        if (_streaming == null || text.Length + reasoning.Length == 0) return;
        if (reasoning.Length > 0) _streaming.Reasoning += reasoning;
        if (text.Length > 0) _streaming.Text += text;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTurnCompleted(TurnResult result)
    {
        FlushDelta();
        if (_streaming != null)
        {
            _streaming.IsStreaming = false;
            // What was kept: a Live reply cut by its length cap loses its unfinished last sentence.
            if (result.AssistantMessage is { } saved) _streaming.Text = saved.Content;
            _streaming.Interrupted = result.Outcome == TurnOutcome.Cancelled;
            if (_streaming.Text.Length == 0) Messages.Remove(_streaming);
            _streaming = null;
        }
        if (result.Outcome == TurnOutcome.Failed)
        {
            Messages.Add(new MessageViewModel(ChatRole.System, result.Error ?? "Generation failed.", DateTimeOffset.Now, InputSource.Text, null, AssistantName));
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
        if (result.Stats != null) _lastStats = result.Stats;
        DiagnosticsText = BuildDiagnostics();

        var current = Conversations.FirstOrDefault(c => c.Id == _session.CurrentConversationId);
        if (current != null)
        {
            current.UpdatedAt = DateTimeOffset.Now;
            var idx = Conversations.IndexOf(current);
            if (idx > 0)
            {
                _suppressSelection = true;
                Conversations.Move(idx, 0);
                SelectedConversation = current;
                _suppressSelection = false;
            }
        }
    }

    // ---------------- Conversations ----------------

    /// <summary>The conversations the memory searches (the list marks them).</summary>
    private IReadOnlySet<long> _inMemory = new HashSet<long>();

    /// <summary>Marks the conversations the assistant recalls (their note is in the memory search).</summary>
    private async Task RefreshMemoryMarksAsync()
    {
        try
        {
            _inMemory = await _memory.ConversationsInMemoryAsync();
        }
        catch (OperationCanceledException) { return; }
        foreach (var item in Conversations) MarkMemory(item);
    }

    private void MarkMemory(ConversationItemViewModel item)
    {
        item.InMemory = _inMemory.Contains(item.Id);
        item.MemoryTip = !_options.Memory.Enabled ? "The memory is turned off (Settings › Memory)."
            : item.InMemory ? $"In memory: {AssistantName} recalls this conversation when it is relevant."
            : $"Not in memory: {AssistantName} writes a conversation's note once it goes quiet, and recalls only the " +
              $"{_options.Memory.MaxConversationNotes} newest conversations.";
    }

    private void OnConversationCreated(Conversation c)
    {
        var item = new ConversationItemViewModel(c);
        MarkMemory(item);
        Conversations.Insert(0, item);
        _suppressSelection = true;
        SelectedConversation = item;
        ConversationTitle = item.Title;
        _suppressSelection = false;
    }

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        if (_suppressSelection) return;
        if (value != null) SelectedPage = AppPage.Chat;
        _ = OpenConversationAsync(value);
    }

    private async Task OpenConversationAsync(ConversationItemViewModel? item)
    {
        _session.SelectConversation(item?.Id);
        Messages.Clear();
        _streaming = null;
        ConversationTitle = item?.Title ?? "New conversation";
        if (item == null) return;
        try
        {
            var messages = await _store.GetMessagesAsync(item.Id);
            if (SelectedConversation != item) return;
            foreach (var m in messages) Messages.Add(MessageViewModel.From(m, AssistantName));
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load messages");
            BannerText = "Could not load this conversation.";
        }
    }

    [RelayCommand]
    private void NewConversation()
    {
        SelectedPage = AppPage.Chat;
        SelectedConversation = null;
        _ = OpenConversationAsync(null);
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItemViewModel? item)
    {
        item ??= SelectedConversation;
        if (item == null) return;
        try
        {
            // The one being answered stops first: its reply would be saved into a conversation that is gone.
            if (_session.CurrentConversationId == item.Id) Stop();
            await _memory.DeleteConversationNoteAsync(item.Id); // the note goes with the conversation
            await _store.DeleteAsync(item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not delete conversation {Id}", item.Id);
            BannerText = $"Could not delete the conversation: {ex.Message}";
            return;
        }
        _logger.LogInformation("Deleted conversation {Id}", item.Id);
        Conversations.Remove(item);
        if (SelectedConversation == item || _session.CurrentConversationId == item.Id) NewConversation();
    }

    [RelayCommand]
    private void BeginRename(ConversationItemViewModel? item)
    {
        item ??= SelectedConversation;
        if (item == null) return;
        item.EditTitle = item.Title;
        item.IsRenaming = true;
    }

    [RelayCommand]
    private async Task CommitRenameAsync(ConversationItemViewModel? item)
    {
        if (item == null || !item.IsRenaming) return;
        item.IsRenaming = false;
        var title = item.EditTitle.Trim();
        if (title.Length == 0 || title == item.Title) return;
        await _store.RenameAsync(item.Id, title);
        item.Title = title;
        if (SelectedConversation == item) ConversationTitle = title;
    }

    [RelayCommand]
    private static void CancelRename(ConversationItemViewModel? item)
    {
        if (item != null) item.IsRenaming = false;
    }

    partial void OnInputTextChanged(string value)
    {
        _voice.SetTyping(IsContinuous && !string.IsNullOrWhiteSpace(value));
        if (IsContinuous) OnVoiceState(_voice.State);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = SearchAsync(value, _searchCts.Token);
    }

    private async Task SearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(200, ct);
            if (string.IsNullOrWhiteSpace(query))
            {
                await LoadConversationsAsync();
                return;
            }
            var hits = await _store.SearchAsync(query, ct);
            if (ct.IsCancellationRequested) return;
            Conversations.Clear();
            foreach (var h in hits) Conversations.Add(new ConversationItemViewModel(h.Conversation) { Snippet = h.Snippet });
            await RefreshMemoryMarksAsync();
        }
        catch (OperationCanceledException) { }
    }

    // ---------------- Voice ----------------

    public async Task BeginPushToTalkAsync()
    {
        if (!VoiceAvailable || ShowWelcome) return;
        await _voice.BeginPushToTalkAsync();
    }

    public async Task EndPushToTalkAsync() => await _voice.EndPushToTalkAsync();

    partial void OnReplyModeChanged(ReplyMode oldValue, ReplyMode newValue)
    {
        if (newValue != ReplyMode.Live) _lastNonLiveMode = newValue;
        _voice.SetTyping(newValue == ReplyMode.Live && !string.IsNullOrWhiteSpace(InputText));
        if (newValue == ReplyMode.Live) _ = _voice.StartContinuousAsync();
        // The reply being spoken goes on in the new mode; Stop (the Send button) or Esc ends it.
        else if (oldValue == ReplyMode.Live) _ = _voice.StopContinuousAsync(keepReply: true);
        if (!_modeFollowsShortcut) _settings.Set("Voice", "ReplyMode", newValue.ToString());
    }

    /// <summary>Whether the tray menu can turn live mode on (it can always turn it off).</summary>
    public bool CanToggleLive => LiveOffered && VoiceAvailable && !ShowWelcome && !IsLearning;

    /// <summary>Tray menu: turns live mode on, or back to the previous mode, like the mode selector does.</summary>
    public void ToggleLive()
    {
        if (IsLiveMode) ReplyMode = _lastNonLiveMode;
        else if (CanToggleLive) ReplyMode = ReplyMode.Live;
    }

    /// <summary>A way to talk was turned on or off in the settings: a mode no longer offered falls back to Text.</summary>
    private void OnWaysToTalkChanged()
    {
        OnPropertyChanged(nameof(ReadAloudOffered));
        OnPropertyChanged(nameof(LiveOffered));
        OnPropertyChanged(nameof(CanToggleLive));
        if (!ReadAloudOffered && _lastNonLiveMode == ReplyMode.ReadAloud) _lastNonLiveMode = ReplyMode.Text;
        if ((ReplyMode == ReplyMode.ReadAloud && !ReadAloudOffered) || (ReplyMode == ReplyMode.Live && !LiveOffered))
            ReplyMode = ReplyMode.Text;
    }

    /// <summary>Mirrors live mode turned on or off by an assistant's shortcut in the mode selector.</summary>
    private void OnLiveStateChanged(LiveState state)
    {
        var target = state switch
        {
            LiveState.Live when ReplyMode != ReplyMode.Live => ReplyMode.Live,
            LiveState.Off when ReplyMode == ReplyMode.Live => _lastNonLiveMode,
            _ => (ReplyMode?)null,
        };
        if (target is not { } mode) return;
        _modeFollowsShortcut = true;
        try { ReplyMode = mode; }
        finally { _modeFollowsShortcut = false; }
    }

    /// <summary>A shortcut opened a new conversation: show it (the window may be hidden, it is ready when shown).</summary>
    private void OnSessionConversationChanged(long? conversationId)
    {
        if (conversationId != null || _session.CurrentConversationId != null || SelectedConversation == null) return;
        _suppressSelection = true;
        SelectedConversation = null;
        _suppressSelection = false;
        Messages.Clear();
        _streaming = null;
        ConversationTitle = "New conversation";
    }

    private void OnVoiceState(VoiceState s)
    {
        IsRecording = s == VoiceState.Recording;
        VoiceText = s switch
        {
            VoiceState.Listening when _voice.IsTyping => "⌨ Typing · microphone paused",
            VoiceState.Listening => "◉ Listening",
            VoiceState.Recording => "● Recording",
            VoiceState.Transcribing => "… Transcribing",
            VoiceState.Thinking => "◌ Thinking",
            VoiceState.Speaking => "♪ Speaking",
            VoiceState.Error => "Voice: " + (_voice.LastError ?? "error"),
            _ when _stt.State == ComponentState.Unavailable => "Voice: unavailable (" + (_stt.LastError ?? "speech recognition failed") + ")",
            // Speech recognition loads with the AI: not loaded means the AI is off, not that it is loading.
            _ when _stt.State == ComponentState.NotInitialized => "Voice: off (turn the AI on)",
            _ when _stt.State != ComponentState.Ready => "Voice: loading…",
            _ when Settings.Microphones.Count == 0 => "Voice: no microphone found",
            _ => IsContinuous ? "◉ Listening" : "", // idle: nothing to show
        };
        if (s == VoiceState.Error && IsContinuous) ReplyMode = ReplyMode.Text;
    }

    // Fast attack, ~25 dB/s release, like a hardware VU meter.
    private float _displayedDb = AudioMath.SilenceDb;

    private void OnInputLevel(float db, bool reset = false)
    {
        _displayedDb = reset ? db : Math.Max(db, _displayedDb - 1.7f);
        var shown = _displayedDb;
        MicLevel = Math.Clamp((shown + 60) / 60.0, 0, 1);
        MicLevelText = shown <= -90 ? "silence" : $"{shown:F0} dB";
        MicLevelBrush = shown > -6 ? LevelClip : shown > -18 ? LevelHot : LevelOk;
    }

    [RelayCommand]
    private void ContinueWelcome() => WelcomeIntro = false;

    // ---------------- Top bar: assistants and pages ----------------

    [RelayCommand]
    private void OpenPage(AppPage page) => SelectedPage = page;

    private async Task LoadAssistantTabsAsync()
    {
        try
        {
            _profiles = await _manager.ListAsync();
            AssistantTabs.Clear();
            foreach (var a in _profiles) AssistantTabs.Add(new AssistantTabViewModel(a.Id, a.Name, a.Hotkey?.ToString(), a.Avatar));
            UpdateAssistantTabs();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not list the assistants");
        }
    }

    private IReadOnlyList<AssistantProfile> _profiles = [];

    /// <summary>
    /// Marks the active assistant's tab, the one listening in live mode, and whose AI is on: the loaded language model is
    /// its own (assistants with the same model share it). Also refreshes the AI button above the conversations.
    /// </summary>
    private void UpdateAssistantTabs()
    {
        var active = _assistant.Current?.Id;
        var live = _live.State == LiveState.Live ? _live.Assistant?.Id : null;
        foreach (var tab in AssistantTabs)
        {
            tab.IsActive = tab.Id == active;
            tab.IsSelected = tab.IsActive && IsChatPage;
            tab.IsLive = tab.Id == live;
            tab.IsOnline = _manager.IsModelLoaded(_profiles.FirstOrDefault(p => p.Id == tab.Id));
            // Yellow while anything of the active assistant loads: its language model or its voice.
            tab.IsLoading = tab.IsActive && (_llm.State == LanguageModelState.Loading || _manager.IsLoadingVoice);
        }

        var current = _assistant.Current;
        AiOn = _manager.IsActiveModelLoaded;
        AiLoading = current != null && _llm.State == LanguageModelState.Loading;
        var model = AiOn && _llm.Info is { } info ? info.DisplayName
            : current == null ? ""
            : _models.Find(current.ModelId)?.DisplayName ?? "Best installed model";
        AnyLoading = AiLoading || (current != null && _manager.IsLoadingVoice);
        AiStatusText = AiLoading ? "Starting" : IsLearning ? "Learning" : AnyLoading ? "Loading voice" : AiOn ? "On"
            : current != null && _llm.State == LanguageModelState.Failed ? "Failed"
            : "Off";
        AiModelText = model;

        // While the assistant's model downloads, its Brain is off and says how far the download is.
        IsDownloading = _models.IsAnyDownloading;
        var package = current == null ? null : _models.Find(current.ModelId);
        if (package != null && _models.DownloadProgress(package.Id) is { } progress)
        {
            AiStatusText = "Downloading";
            var total = package.SizeBytes / 1e6;
            AiModelText = $"{total * progress:N0}/{total:N0} MB - {progress:P0}";
        }
        UpdateSenses();
    }

    /// <summary>The active assistant's AI (language model) is on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiButtonTip), nameof(AiStatusBrush), nameof(SendTip))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(ToggleHearingCommand), nameof(ToggleSpeakerCommand), nameof(UpdateMemoryCommand))]
    private bool _aiOn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiButtonTip), nameof(AiStatusBrush))]
    private bool _aiLoading;

    [ObservableProperty] private string _aiStatusText = "";

    /// <summary>The active assistant's model or voice is loading (yellow dot).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiStatusBrush))]
    private bool _anyLoading;
    /// <summary>The active assistant's language model, shown under the on/off status.</summary>
    [ObservableProperty] private string _aiModelText = "";


    public string AiButtonText => "⏻  Brain";
    public string AiButtonTip => AiOn || AiLoading
        ? "Turns the Brain off: unloads the language model, memory search, hearing and speaker"
        : "Turns the Brain on: loads this assistant's language model and memory search (one runs at a time), then its hearing and speaker if they are on";
    public IBrush AiStatusBrush => AnyLoading || IsLearning ? LoadingBrush : AiOn ? LevelOk : LevelClip;

    /// <summary>A model is downloading: the controls wait until it is done (what they would start may not be there yet).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleAiCommand), nameof(ToggleHearingCommand), nameof(ToggleSpeakerCommand), nameof(UpdateMemoryCommand))]
    private bool _isDownloading;

    private bool CanToggleAi() => !IsDownloading;

    // ---------------- Hearing and Speaker (loaded with the Brain, each one as the user left it) ----------------

    [ObservableProperty] private string _hearingStatusText = "Off";
    [ObservableProperty] private IBrush _hearingStatusBrush = LevelClip;
    /// <summary>The speech recognition model, under the state (like the Brain's language model).</summary>
    [ObservableProperty] private string _hearingDetailText = "";
    [ObservableProperty] private string _speakerStatusText = "Off";
    [ObservableProperty] private IBrush _speakerStatusBrush = LevelClip;
    /// <summary>The assistant's voice, under the state.</summary>
    [ObservableProperty] private string _speakerDetailText = "";

    /// <summary>The voice is loaded: Read aloud and Live can speak.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveUsable))]
    private bool _speakerReady;

    /// <summary>Live needs to hear and to speak.</summary>
    public bool LiveUsable => VoiceUsable && SpeakerReady;

    private void UpdateSenses()
    {
        var current = _assistant.Current;
        var on = AiOn;
        HearingStatusText = !on ? "Off"
            : _stt.State == ComponentState.Initializing ? "Loading"
            : _stt.State == ComponentState.Ready ? "On"
            : current?.Hearing == true ? "Unavailable" : "Off";
        HearingStatusBrush = HearingStatusText switch { "On" => LevelOk, "Loading" => LoadingBrush, _ => LevelClip };

        SpeakerReady = on && _tts.State == ComponentState.Ready && _tts.Voice != null;
        SpeakerStatusText = !on ? "Off"
            : _manager.IsLoadingVoice ? "Loading"
            : SpeakerReady ? "On"
            : current?.Speaker == true ? "Unavailable" : "Off";
        HearingDetailText = current == null ? "" : _models.Find(_options.SpeechToText.Model)?.DisplayName ?? "Speech recognition";
        SpeakerDetailText = current == null ? "" : _models.FindVoice(current.VoiceId)?.Label ?? current.VoiceId;
        SpeakerStatusBrush = SpeakerReady ? LevelOk : _manager.IsLoadingVoice && on ? LoadingBrush : LevelClip;

        // Without the voice nothing can be read aloud or said live: back to text.
        if (on && !_manager.IsLoadingVoice && current?.Speaker == false && (IsReadAloudMode || IsLiveMode)) ReplyMode = ReplyMode.Text;
    }

    private bool CanToggleSense() => AiOn && !IsLearning && !IsDownloading;

    /// <summary>Turns the assistant's hearing (speech recognition) on or off; remembered for the next time its Brain turns on.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSense))]
    private Task ToggleHearingAsync() => SetSensesAsync(hearing: current => !current.Hearing, speaker: current => current.Speaker);

    /// <summary>Turns the assistant's speaker (its voice) on or off; remembered for the next time its Brain turns on.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleSense))]
    private Task ToggleSpeakerAsync() => SetSensesAsync(hearing: current => current.Hearing, speaker: current => !current.Speaker);

    private async Task SetSensesAsync(Func<AssistantProfile, bool> hearing, Func<AssistantProfile, bool> speaker)
    {
        if (_assistant.Current is not { } current) return;
        var hear = hearing(current);
        var speak = speaker(current);
        if (!hear && IsLiveMode) ReplyMode = _lastNonLiveMode;
        if (!speak) Stop(); // nothing is said any more
        try
        {
            await Task.Run(() => _manager.SetSensesAsync(current, hear, speak));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not turn the hearing or speaker of assistant {Id} on or off", current.Id);
            BannerText = $"Could not change {current.Name}'s hearing or speaker: {ex.Message}";
        }
        UpdateAssistantTabs();
        UpdateStatus();
    }

    // ---------------- Update memory ----------------

    /// <summary>The first click asks "Are you sure?" (for a few seconds); the second one starts.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UpdateMemoryText))] private bool _confirmUpdateMemory;
    [ObservableProperty] private string? _updateMemoryStatus;
    private CancellationTokenSource? _confirmUpdateTimeout;

    public string UpdateMemoryText => ConfirmUpdateMemory ? "Are you sure?" : "Update memory";

    private bool CanUpdateMemory() => AiOn && !IsLearning && !IsBusy && !IsDownloading;

    /// <summary>
    /// Writes the notes of the conversations not summarized yet (the ones already up to date are skipped), then the
    /// profile, the identity note and the index, now. The chat waits until it is done.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateMemory), AllowConcurrentExecutions = true)]
    private async Task UpdateMemoryAsync()
    {
        if (_assistant.Current is not { } current) return;
        if (!ConfirmUpdateMemory)
        {
            ConfirmUpdateMemory = true;
            _confirmUpdateTimeout = new CancellationTokenSource();
            try { await Task.Delay(TimeSpan.FromSeconds(4), _confirmUpdateTimeout.Token); }
            catch (OperationCanceledException) { return; }
            ConfirmUpdateMemory = false;
            return;
        }
        _confirmUpdateTimeout?.Cancel();
        ConfirmUpdateMemory = false;
        UpdateMemoryStatus = "Starting…";
        var progress = new Progress<string>(step => UpdateMemoryStatus = step);
        try
        {
            var jobs = await Task.Run(() => _memoryWorker.UpdateMemoryAsync(current.Id, progress));
            UpdateMemoryStatus = jobs == 0 ? "Nothing to update" : $"Updated ({jobs} {(jobs == 1 ? "step" : "steps")})";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update memory failed");
            UpdateMemoryStatus = $"Failed: {ex.Message}";
        }
    }

    private static readonly IBrush LoadingBrush = new SolidColorBrush(Color.Parse(StatusColors.Background));

    /// <summary>
    /// The button above the conversations: turns the active assistant's AI (its language model) on or off. Only this
    /// loads or unloads a model; switching tabs never does. One model runs at a time, so turning one on unloads another
    /// assistant's. Turning off first finishes the assistant's memory work; meanwhile the button turns it back on
    /// (concurrent: it must stay clickable while turning off runs).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleAi), AllowConcurrentExecutions = true)]
    private async Task ToggleAiAsync()
    {
        if (_assistant.Current is not { } current) return;
        try
        {
            if (AiOn || AiLoading)
            {
                Stop();
                if (IsLiveMode) ReplyMode = _lastNonLiveMode; // live mode needs the model
                await Task.Run(() => _manager.UnloadModelAsync());
            }
            else
            {
                await Task.Run(() => _manager.LoadModelAsync(current));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not turn the AI of assistant {Id} on or off", current.Id);
            BannerText = $"Could not turn {current.Name}'s AI on or off: {ex.Message}";
        }
        UpdateAssistantTabs();
        UpdateStatus();
    }

    /// <summary>
    /// Shows the assistant's chat. Never loads or unloads a model (the AI button does); its voice loads in the
    /// background (yellow dot) only if its AI is on. The tabs stay clickable meanwhile: the last click wins.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectAssistantAsync(AssistantTabViewModel? tab)
    {
        if (tab == null) return;
        SelectedPage = AppPage.Chat;
        if (tab.Id == _assistant.Current?.Id) return;
        try
        {
            var profile = (await _manager.ListAsync()).FirstOrDefault(a => a.Id == tab.Id);
            if (profile != null) await Task.Run(() => _manager.SwitchAsync(profile, loadModel: false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not switch assistant");
            BannerText = $"Could not switch to {tab.Name}: {ex.Message}";
        }
        UpdateStatus();
    }

    partial void OnSelectedPageChanged(AppPage oldValue, AppPage newValue)
    {
        UpdateAssistantTabs();
        if (oldValue == AppPage.Settings) Settings.OnClosed();
        switch (newValue)
        {
            case AppPage.Settings:
                Settings.Models.Refresh();
                _ = Settings.Assistants.RefreshAsync();
                _ = LoadMemoriesAsync();
                break;
            case AppPage.Diagnostics:
                DiagnosticsText = BuildDiagnostics();
                break;
        }
    }

    // ---------------- Memory & diagnostics ----------------

    /// <summary>Diagnostics: how many notes the active assistant has (conversation notes, and whether a profile exists).</summary>
    private async Task LoadMemoriesAsync()
    {
        if (_assistant.Current == null)
        {
            _memoryNoteCount = 0;
            _hasMemoryProfile = false;
            return;
        }
        try
        {
            var notes = await _memory.ListNotesAsync();
            _memoryNoteCount = notes.Count(n => n.Kind == NoteKind.Conversation);
            _hasMemoryProfile = notes.Any(n => n.Kind == NoteKind.Profile);
            DiagnosticsText = BuildDiagnostics();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the memory notes");
        }
    }

    private int _memoryNoteCount;
    private bool _hasMemoryProfile;

    private string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        var gpu = _startup.Gpu;
        sb.AppendLine("GPU");
        if (gpu != null)
        {
            sb.AppendLine($"  {gpu.Name}");
            sb.AppendLine($"  VRAM            {gpu.TotalMemoryMb / 1024.0:F1} GB total");
            sb.AppendLine($"  Driver / CUDA   {gpu.DriverVersion} / {gpu.CudaVersion}   compute {gpu.ComputeCapability}");
        }
        else sb.AppendLine("  none detected (CPU inference)");

        sb.AppendLine().AppendLine("Language model");
        if (_llm.Info is { } i)
        {
            sb.AppendLine($"  {i.DisplayName}");
            sb.AppendLine($"  Quantization    {i.Quantization}");
            sb.AppendLine($"  Context         {i.ContextSize:N0} tokens");
            sb.AppendLine($"  GPU layers      {(i.GpuLayers < 0 ? "all" : i.GpuLayers.ToString())}   backend {i.Backend}");
            sb.AppendLine($"  VRAM            est. {i.EstimatedVramMb:N0} MB   measured {(i.MeasuredVramMb is { } m ? $"{m:N0} MB" : "n/a")}");
            sb.AppendLine($"  File            {Path.GetFileName(i.FilePath)} ({i.FileSizeBytes / 1e9:F2} GB)");
            sb.AppendLine($"  Load time       {i.LoadTime.TotalSeconds:F1} s");
        }
        else sb.AppendLine($"  {_llm.State}{(_llm.LastError != null ? ": " + _llm.LastError : "")}");
        if (_lastStats is { } st)
            sb.AppendLine($"  Last reply      {st.GeneratedTokens} tok, {st.TokensPerSecond:F1} tok/s, first token {st.TimeToFirstTokenMs:F0} ms");

        sb.AppendLine().AppendLine("Speech");
        sb.AppendLine($"  Recognition     {(_stt.State == ComponentState.Ready ? _stt.Description : _stt.State + " " + _stt.LastError)}");
        sb.AppendLine($"  Synthesis       {(_tts.State == ComponentState.Ready ? "Kokoro via sherpa-onnx (CPU): " + _tts.Voice?.DisplayName : _tts.State + " " + _tts.LastError)}");
        sb.AppendLine($"  Microphone      {Settings.SelectedMicrophone?.Name ?? "none"}");
        sb.AppendLine($"  Speaker         {Settings.SelectedSpeaker?.Name ?? "none"}");

        sb.AppendLine().AppendLine("Memory");
        sb.AppendLine($"  Notes           {_memoryNoteCount} conversation notes, {(_hasMemoryProfile ?"profile" : "no profile yet")}, search {(_embeddings.IsAvailable ? "semantic (" + _embeddings.ModelId + ") + words" : "words only (" + _embeddings.LastError + ")")}");
        sb.AppendLine($"  Folder          {_memory.Root}");
        sb.AppendLine($"  Database        {_paths.DatabasePath}");
        sb.AppendLine($"  Logs            {_paths.LogsDirectory}");

        sb.AppendLine().AppendLine("Privacy");
        sb.AppendLine("  Fully offline. Engines listen on 127.0.0.1 only; no telemetry, no cloud APIs.");
        return sb.ToString();
    }

    private static void Ui(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
