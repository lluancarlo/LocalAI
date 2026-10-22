using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Llm;
using LocalAI.Core.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Memory.Vault;

/// <summary>Whether the assistant is working for the user right now (replying, recording, transcribing or speaking).</summary>
public interface IAssistantActivity
{
    bool IsBusy { get; }
    /// <summary>Raised (on any thread) when <see cref="IsBusy"/> changes.</summary>
    event EventHandler<bool>? BusyChanged;
}

/// <summary>The assistant is busy while a turn runs or the voice conversation records, transcribes, thinks or speaks.</summary>
public sealed class AssistantActivity : IAssistantActivity, IDisposable
{
    private readonly AssistantSession _session;
    private readonly ILiveVoice _voice;
    private volatile bool _turn;
    private volatile bool _voiceBusy;
    private readonly Lock _gate = new();
    private bool _busy;

    public AssistantActivity(AssistantSession session, ILiveVoice voice)
    {
        _session = session;
        _voice = voice;
        _session.BusyChanged += OnTurn;
        _voice.StateChanged += OnVoice;
    }

    public bool IsBusy => _turn || _voiceBusy;

    public event EventHandler<bool>? BusyChanged;

    private void OnTurn(object? sender, bool busy)
    {
        _turn = busy;
        Notify();
    }

    private void OnVoice(object? sender, VoiceState state)
    {
        _voiceBusy = state is VoiceState.Recording or VoiceState.Transcribing or VoiceState.Thinking or VoiceState.Speaking;
        Notify();
    }

    private void Notify()
    {
        var busy = IsBusy;
        lock (_gate)
        {
            if (busy == _busy) return;
            _busy = busy;
        }
        BusyChanged?.Invoke(this, busy);
    }

    public void Dispose()
    {
        _session.BusyChanged -= OnTurn;
        _voice.StateChanged -= OnVoice;
    }
}

/// <summary>
/// Runs the memory's background work (writing conversation notes, merging the profile, indexing notes) from a queue
/// kept in the database, so nothing is lost when the app closes. The assistant always comes first: work starts only
/// after the assistant has been idle for a moment, and the moment the user sends a message or speaks, the running job
/// is cancelled (the language model stops at once) and goes back to the queue.
/// </summary>
public sealed class MemoryWorker : IAsyncDisposable
{
    /// <summary>Quiet time before background work starts.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    /// <summary>Delay before the profile is merged after a conversation note, so several notes go in one merge.</summary>
    private static readonly TimeSpan ProfileDelay = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 5;
    private const string NotesSinceKey = "notes-since";
    private const string LegacyMigratedKey = "legacy-memory-migrated";

    private readonly MemoryVault _vault;
    private readonly IVaultIndex _index;
    private readonly IVaultStorage _storage;
    private readonly IAssistantStore _assistants;
    private readonly IAssistantActivity _activity;
    private readonly AssistantSession _session;
    private readonly AssistantContext _context;
    private readonly ILanguageModel _llm;
    private readonly MemoryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MemoryWorker> _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    /// <summary>Held while a job runs, so <see cref="PauseAsync"/> can wait for it to end.</summary>
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _jobCts;
    private DateTimeOffset _lastBusy;
    private (long ConversationId, long AssistantId)? _currentConversation;
    private Task? _loop;
    private volatile bool _paused;

    public MemoryWorker(MemoryVault vault, IVaultIndex index, IVaultStorage storage, IAssistantStore assistants,
        IAssistantActivity activity, AssistantSession session, AssistantContext context, ILanguageModel llm,
        IOptions<LocalAiOptions> options, TimeProvider time, ILogger<MemoryWorker> logger)
    {
        _vault = vault;
        _index = index;
        _storage = storage;
        _assistants = assistants;
        _activity = activity;
        _session = session;
        _context = context;
        _llm = llm;
        _options = options.Value.Memory;
        _time = time;
        _logger = logger;
        _lastBusy = time.GetUtcNow();
        _activity.BusyChanged += OnBusyChanged;
        _session.UserMessageAdded += OnUserMessage;
        _session.ConversationChanged += OnConversationChanged;
        _storage.Changed += OnNoteChanged;
    }

    /// <summary>Raised when a job starts or ends (for tests and diagnostics).</summary>
    public event EventHandler<MemoryJob>? JobStarted;
    public event EventHandler<(MemoryJob Job, string Outcome)>? JobFinished;

    /// <summary>True while a memory job runs (the app shows that something happens in the background).</summary>
    public bool IsWorking { get; private set; }

    /// <summary>Raised (on a background thread) when <see cref="IsWorking"/> changes.</summary>
    public event EventHandler<bool>? WorkingChanged;

    private void SetWorking(bool working)
    {
        if (IsWorking == working) return;
        IsWorking = working;
        WorkingChanged?.Invoke(this, working);
    }

    public void Start()
    {
        if (_loop != null || !_options.Enabled) return;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>
    /// Stops starting jobs, cancels the running one and waits for it to end, e.g. before the notes folder is moved.
    /// </summary>
    public async Task PauseAsync()
    {
        _paused = true;
        CancelJob();
        await _running.WaitAsync().ConfigureAwait(false);
        _running.Release();
    }

    public void Resume()
    {
        _paused = false;
        Wake();
    }

    private void Wake() => _wake.Release();

    /// <summary>Re-checks the queue now (tests advance their clock, then nudge).</summary>
    internal void Nudge() => Wake();

    /// <summary>Checks every assistant's notes against the index again (e.g. after the embedding model changed).</summary>
    public async Task RescanAsync(CancellationToken ct = default)
    {
        foreach (var a in await _assistants.ListAsync(ct).ConfigureAwait(false))
            await _index.EnqueueAsync(MemoryJobKind.IndexNote, a.Id, null, null, _time.GetUtcNow(), ct).ConfigureAwait(false);
        Wake();
    }

    // ---------------- Triggers ----------------

    private void OnBusyChanged(object? sender, bool busy)
    {
        _lastBusy = _time.GetUtcNow();
        if (busy) CancelJob(); // the user comes first
        else Wake();
    }

    private void CancelJob()
    {
        lock (_gate)
        {
            try { _jobCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void OnUserMessage(object? sender, Conversations.StoredMessage message)
    {
        if (_context.Current is { } assistant) _currentConversation = (message.ConversationId, assistant.Id);
    }

    /// <summary>Leaving a conversation writes its note without waiting for the idle time.</summary>
    private void OnConversationChanged(object? sender, long? conversationId)
    {
        if (_currentConversation is not { } previous || previous.ConversationId == conversationId) return;
        _currentConversation = null;
        _ = EnqueueSafelyAsync(MemoryJobKind.SummarizeConversation, previous.AssistantId, previous.ConversationId, null, _time.GetUtcNow());
    }

    private void OnNoteChanged(object? sender, string path) => _ = OnNoteChangedAsync(path);

    private async Task OnNoteChangedAsync(string path)
    {
        try
        {
            if (await _vault.AssistantOfPathAsync(path, _stop.Token).ConfigureAwait(false) is { } assistantId)
                await EnqueueSafelyAsync(MemoryJobKind.IndexNote, assistantId, null, path, _time.GetUtcNow()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task EnqueueSafelyAsync(MemoryJobKind kind, long assistantId, long? conversationId, string? path, DateTimeOffset notBefore)
    {
        try
        {
            await _index.EnqueueAsync(kind, assistantId, conversationId, path, notBefore, _stop.Token).ConfigureAwait(false);
            Wake();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not queue memory work"); }
    }

    // ---------------- Loop ----------------

    private async Task LoopAsync(CancellationToken stop)
    {
        DateTimeOffset since;
        try
        {
            since = await PrepareAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The memory could not start");
            return;
        }

        var lastScan = DateTimeOffset.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = _time.GetUtcNow();
                if (now - lastScan >= ScanInterval)
                {
                    lastScan = now;
                    var idleBefore = now - TimeSpan.FromMinutes(Math.Max(0, _options.ConversationIdleMinutes));
                    foreach (var (conversationId, assistantId) in await _index.ConversationsNeedingNotesAsync(since, idleBefore, stop).ConfigureAwait(false))
                        await _index.EnqueueAsync(MemoryJobKind.SummarizeConversation, assistantId, conversationId, null, now, stop).ConfigureAwait(false);
                }

                if (_paused || !IsIdleLongEnough(now) || await _index.NextJobAsync(now, stop).ConfigureAwait(false) is not { } job)
                {
                    await WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }
                await RunAsync(job, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory work loop failed");
                await WaitAsync(stop).ConfigureAwait(false);
            }
        }
    }

    private bool IsIdleLongEnough(DateTimeOffset now) => !_activity.IsBusy && now - _lastBusy >= IdleDelay;

    private async Task WaitAsync(CancellationToken stop)
    {
        try { await _wake.WaitAsync(PollInterval, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
    }

    /// <summary>
    /// First run: notes are written only for conversations from now on. Every run: the old memory moves into the
    /// profiles once, and each assistant's notes are checked against the index (edits made while the app was closed).
    /// </summary>
    private async Task<DateTimeOffset> PrepareAsync(CancellationToken stop)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset since;
        if (await _index.GetStateAsync(NotesSinceKey, stop).ConfigureAwait(false) is { } stored &&
            DateTimeOffset.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            since = parsed;
        }
        else
        {
            since = now;
            await _index.SetStateAsync(NotesSinceKey, now.ToString("O"), stop).ConfigureAwait(false);
        }

        var all = await _assistants.ListAsync(stop).ConfigureAwait(false);
        if (await _index.GetStateAsync(LegacyMigratedKey, stop).ConfigureAwait(false) == null)
        {
            foreach (var a in all) await _index.EnqueueAsync(MemoryJobKind.MigrateLegacyFacts, a.Id, null, null, now, stop).ConfigureAwait(false);
            await _index.SetStateAsync(LegacyMigratedKey, now.ToString("O"), stop).ConfigureAwait(false);
        }
        foreach (var a in all) await _index.EnqueueAsync(MemoryJobKind.IndexNote, a.Id, null, null, now, stop).ConfigureAwait(false);
        return since;
    }

    private async Task RunAsync(MemoryJob job, CancellationToken stop)
    {
        await _running.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            if (!_paused) await RunJobAsync(job, stop).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task RunJobAsync(MemoryJob job, CancellationToken stop)
    {
        var needsModel = job.Kind is MemoryJobKind.SummarizeConversation or MemoryJobKind.ConsolidateProfile;
        if (needsModel && _llm.State != LanguageModelState.Ready)
        {
            await _index.PostponeJobAsync(job.Id, _time.GetUtcNow() + TimeSpan.FromSeconds(30), failed: false, stop).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_gate) _jobCts = cts;
        if (_activity.IsBusy) cts.Cancel(); // the user started something since the check
        JobStarted?.Invoke(this, job);
        SetWorking(true);
        string outcome;
        try
        {
            await ExecuteAsync(job, cts.Token).ConfigureAwait(false);
            await _index.CompleteJobAsync(job.Id, stop).ConfigureAwait(false);
            outcome = "done";
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // Pre-empted by the user: back in the queue, not a failure.
            await _index.PostponeJobAsync(job.Id, _time.GetUtcNow(), failed: false, stop).ConfigureAwait(false);
            outcome = "pre-empted";
            _logger.LogDebug("Memory job {Kind} paused: the assistant is busy", job.Kind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (job.Attempts + 1 >= MaxAttempts)
            {
                await _index.CompleteJobAsync(job.Id, stop).ConfigureAwait(false);
                _logger.LogWarning(ex, "Memory job {Kind} gave up after {Attempts} attempts", job.Kind, MaxAttempts);
                outcome = "given up";
            }
            else
            {
                var retry = _time.GetUtcNow() + TimeSpan.FromMinutes(Math.Pow(2, job.Attempts));
                await _index.PostponeJobAsync(job.Id, retry, failed: true, stop).ConfigureAwait(false);
                _logger.LogWarning(ex, "Memory job {Kind} failed; retrying later", job.Kind);
                outcome = "failed";
            }
        }
        finally
        {
            lock (_gate) _jobCts = null;
            SetWorking(false);
        }
        JobFinished?.Invoke(this, (job, outcome));
    }

    private async Task ExecuteAsync(MemoryJob job, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case MemoryJobKind.MigrateLegacyFacts:
                await _vault.MigrateLegacyFactsAsync(job.AssistantId, ct).ConfigureAwait(false);
                break;
            case MemoryJobKind.SummarizeConversation when job.ConversationId is { } conversationId:
                if (await _vault.WriteConversationNoteAsync(job.AssistantId, conversationId, ct).ConfigureAwait(false))
                    await _index.EnqueueAsync(MemoryJobKind.ConsolidateProfile, job.AssistantId, null, null, _time.GetUtcNow() + ProfileDelay, ct)
                        .ConfigureAwait(false);
                break;
            case MemoryJobKind.ConsolidateProfile:
                await _vault.ConsolidateProfileAsync(job.AssistantId, ct).ConfigureAwait(false);
                break;
            case MemoryJobKind.IndexNote when job.Path is { } path:
                await _vault.IndexNoteAsync(job.AssistantId, path, ct).ConfigureAwait(false);
                break;
            case MemoryJobKind.IndexNote:
                await _vault.ScanAsync(job.AssistantId, ct).ConfigureAwait(false);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _activity.BusyChanged -= OnBusyChanged;
        _session.UserMessageAdded -= OnUserMessage;
        _session.ConversationChanged -= OnConversationChanged;
        _storage.Changed -= OnNoteChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
