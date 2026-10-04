using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Audio;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using LocalAI.Core.Speech;
using LocalAI.Core.Voice;
using LocalAI.Infrastructure;
using LocalAI.LLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.App.ViewModels;

public enum StatusLevel { Ok, Busy, Warning, Error }

/// <summary>
/// Main screen state. Thin layer over <see cref="AssistantSession"/> and <see cref="VoiceConversationController"/>:
/// it never touches engines, the database or native libraries directly.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AssistantSession _session;
    private readonly IConversationStore _store;
    private readonly VoiceConversationController _voice;
    private readonly StartupService _startup;
    private readonly ILanguageModel _llm;
    private readonly LlamaCppEmbeddingService _embeddings;
    private readonly ISpeechToText _stt;
    private readonly ITextToSpeech _tts;
    private readonly MemoryService _memory;
    private readonly UserSettingsStore _settings;
    private readonly LocalAiPaths _paths;
    private readonly LocalAiOptions _options;
    private readonly ILogger<MainWindowViewModel> _logger;
    private MessageViewModel? _streaming;
    private bool _suppressSelection;
    private GenerationStats? _lastStats;
    private CancellationTokenSource? _searchCts;

    public MainWindowViewModel(
        AssistantSession session, IConversationStore store, VoiceConversationController voice, StartupService startup,
        ILanguageModel llm, LlamaCppEmbeddingService embeddings, ISpeechToText stt, ITextToSpeech tts,
        MemoryService memory, UserSettingsStore settings, SettingsViewModel settingsViewModel,
        LocalAiPaths paths, IOptions<LocalAiOptions> options, ILogger<MainWindowViewModel> logger)
    {
        _session = session;
        _store = store;
        _voice = voice;
        _startup = startup;
        _llm = llm;
        _embeddings = embeddings;
        _stt = stt;
        _tts = tts;
        _memory = memory;
        Settings = settingsViewModel;
        _settings = settings;
        _paths = paths;
        _options = options.Value;
        _logger = logger;
        AssistantName = _options.Assistant.Name;
        _isContinuousPreferred = _options.Voice.Mode == VoiceMode.Continuous;

        _session.UserMessageAdded += (_, m) => Ui(() => OnUserMessage(m));
        _session.AssistantMessageStarted += (_, id) => Ui(() => OnAssistantStarted(id));
        _session.AssistantDelta += OnAssistantDelta;
        _session.TurnCompleted += (_, r) => Ui(() => OnTurnCompleted(r));
        _session.ConversationCreated += (_, c) => Ui(() => OnConversationCreated(c));
        _session.BusyChanged += (_, b) => Ui(() => IsBusy = b);
        _voice.StateChanged += (_, s) => Ui(() => OnVoiceState(s));
        _voice.Warning += (_, w) => Ui(() => BannerText = w);
        _voice.Transcribed += (_, _) => Ui(() => BannerText = null);
        _startup.StatusChanged += (_, s) => Ui(() => OnSubsystem(s));
        _memory.MemoriesChanged += (_, _) => Ui(() => _ = LoadMemoriesAsync());
        _voice.InputLevel += (_, db) => Ui(() => OnInputLevel(db));
        _voice.CaptureChanged += (_, open) => Ui(() =>
        {
            IsMicOpen = open;
            if (!open) OnInputLevel(AudioMath.SilenceDb, reset: true);
        });
        Settings.Changed += (_, _) => Ui(UpdateStatus);

        UpdateStatus();
    }

    public string AssistantName { get; }

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];
    public ObservableCollection<MessageViewModel> Messages { get; } = [];
    public ObservableCollection<MemoryItemViewModel> Memories { get; } = [];
    public SettingsViewModel Settings { get; }

    /// <summary>Raised when the view should scroll to the newest message.</summary>
    public event EventHandler? ScrollToEndRequested;

    [ObservableProperty] private ConversationItemViewModel? _selectedConversation;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private string _inputText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SendButtonText))] [NotifyCanExecuteChangedFor(nameof(SendCommand))] private bool _isBusy;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private StatusLevel _statusLevel = StatusLevel.Busy;
    [ObservableProperty] private string _gpuText = "GPU: …";
    [ObservableProperty] private string _modelText = "Model: loading…";
    [ObservableProperty] private string _voiceText = "Voice: starting…";
    [ObservableProperty] private string _conversationTitle = "New conversation";
    [ObservableProperty] private bool _isContinuous;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _speakReplies;
    [ObservableProperty] private bool _voiceAvailable;
    [ObservableProperty] private bool _showDiagnostics;
    [ObservableProperty] private bool _showMemories;
    [ObservableProperty] private bool _showSettings;
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

    private readonly bool _isContinuousPreferred;

    public string SendButtonText => IsBusy ? "Stop" : "Send";

    // ---------------- Startup ----------------

    public async Task LoadConversationsAsync()
    {
        try
        {
            var list = await _store.ListAsync();
            Conversations.Clear();
            foreach (var c in list) Conversations.Add(new ConversationItemViewModel(c));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load conversations");
            BannerText = "Could not open the conversation database. See logs.";
        }
    }

    public void OnStartupCompleted()
    {
        UpdateStatus();
        _ = LoadMemoriesAsync();
        if (_isContinuousPreferred && VoiceAvailable) IsContinuous = true;
    }

    private void OnSubsystem(SubsystemStatus s) => UpdateStatus();

    private void UpdateStatus()
    {
        var gpu = _startup.Gpu;
        GpuText = gpu != null ? $"GPU: {gpu.Name.Replace("NVIDIA GeForce ", "")}" : "GPU: none (CPU)";

        switch (_llm.State)
        {
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

    private void SetStatus(StatusLevel level, string text)
    {
        StatusLevel = level;
        StatusText = text;
    }

    // ---------------- Text chat ----------------

    private bool CanSend() => IsBusy || !string.IsNullOrWhiteSpace(InputText);

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
        if (text.Length == 0) return;
        InputText = "";
        BannerText = null;
        var speak = SpeakReplies && _tts.State == ComponentState.Ready;
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
    private int _flushScheduled;

    private void OnAssistantDelta(object? sender, AssistantDeltaEventArgs e)
    {
        lock (_pendingDelta) _pendingDelta.Append(e.Delta);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(FlushDelta, DispatcherPriority.Background);
    }

    private void FlushDelta()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        string text;
        lock (_pendingDelta)
        {
            text = _pendingDelta.ToString();
            _pendingDelta.Clear();
        }
        if (_streaming == null || text.Length == 0) return;
        _streaming.Text += text;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnTurnCompleted(TurnResult result)
    {
        FlushDelta();
        if (_streaming != null)
        {
            _streaming.IsStreaming = false;
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

    private void OnConversationCreated(Conversation c)
    {
        var item = new ConversationItemViewModel(c);
        Conversations.Insert(0, item);
        _suppressSelection = true;
        SelectedConversation = item;
        ConversationTitle = item.Title;
        _suppressSelection = false;
    }

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        if (_suppressSelection) return;
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
        SelectedConversation = null;
        _ = OpenConversationAsync(null);
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItemViewModel? item)
    {
        item ??= SelectedConversation;
        if (item == null) return;
        await _store.DeleteAsync(item.Id);
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
        }
        catch (OperationCanceledException) { }
    }

    // ---------------- Voice ----------------

    public async Task BeginPushToTalkAsync()
    {
        if (!VoiceAvailable) return;
        await _voice.BeginPushToTalkAsync();
    }

    public async Task EndPushToTalkAsync() => await _voice.EndPushToTalkAsync();

    partial void OnIsContinuousChanged(bool value)
    {
        _ = value ? _voice.StartContinuousAsync() : _voice.StopContinuousAsync();
        _settings.Set("Voice", "Mode", value ? nameof(VoiceMode.Continuous) : nameof(VoiceMode.PushToTalk));
    }

    private void OnVoiceState(VoiceState s)
    {
        IsRecording = s == VoiceState.Recording;
        var mic = Settings.SelectedMicrophone?.Name ?? "no microphone";
        VoiceText = s switch
        {
            VoiceState.Listening => "◉ Listening",
            VoiceState.Recording => "● Recording",
            VoiceState.Transcribing => "… Transcribing",
            VoiceState.Thinking => "◌ Thinking",
            VoiceState.Speaking => "♪ Speaking",
            VoiceState.Error => "Voice: " + (_voice.LastError ?? "error"),
            _ when _stt.State == ComponentState.Unavailable => "Voice: unavailable (" + (_stt.LastError ?? "speech recognition failed") + ")",
            _ when _stt.State != ComponentState.Ready => "Voice: loading…",
            _ when Settings.Microphones.Count == 0 => "Voice: no microphone found",
            _ => IsContinuous ? "◉ Listening" : $"Voice: push-to-talk · {mic}",
        };
        if (s == VoiceState.Error && IsContinuous)
        {
            IsContinuous = false;
        }
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
    private void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
        if (ShowSettings)
        {
            ShowDiagnostics = false;
            ShowMemories = false;
            Settings.RefreshVoices();
        }
        else
        {
            Settings.OnClosed();
        }
    }

    // ---------------- Memory & diagnostics ----------------

    private async Task LoadMemoriesAsync()
    {
        try
        {
            var items = await _memory.ListAsync(CancellationToken.None);
            Memories.Clear();
            foreach (var m in items.OrderByDescending(m => m.CreatedAt)) Memories.Add(new MemoryItemViewModel(m.Id, m.Content, m.CreatedAt));
            DiagnosticsText = BuildDiagnostics();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load memories");
        }
    }

    [RelayCommand]
    private async Task DeleteMemoryAsync(MemoryItemViewModel? item)
    {
        if (item == null) return;
        await _memory.DeleteAsync(item.Id, CancellationToken.None);
    }

    [RelayCommand]
    private void ToggleDiagnostics()
    {
        DiagnosticsText = BuildDiagnostics();
        ShowDiagnostics = !ShowDiagnostics;
        if (ShowDiagnostics) { ShowMemories = false; CloseSettings(); }
    }

    [RelayCommand]
    private void ToggleMemories()
    {
        ShowMemories = !ShowMemories;
        if (ShowMemories) { ShowDiagnostics = false; CloseSettings(); }
    }

    private void CloseSettings()
    {
        if (!ShowSettings) return;
        ShowSettings = false;
        Settings.OnClosed();
    }

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
        sb.AppendLine($"  Synthesis       {(_tts.State == ComponentState.Ready ? "Piper via sherpa-onnx (CPU): " + string.Join(", ", _tts.Voices.Select(v => $"{v.Language}={v.DisplayName}")) : _tts.State + " " + _tts.LastError)}");
        sb.AppendLine($"  Microphone      {Settings.SelectedMicrophone?.Name ?? "none"}");
        sb.AppendLine($"  Speaker         {Settings.SelectedSpeaker?.Name ?? "none"}");

        sb.AppendLine().AppendLine("Memory");
        sb.AppendLine($"  Long-term       {Memories.Count} memories, {(_embeddings.IsAvailable ? "semantic (" + _embeddings.ModelId + ")" : "keyword (" + _embeddings.LastError + ")")}");
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
