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
    private readonly WorkActivity _work;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    /// <summary>Held while a job runs, so <see cref="PauseAsync"/> can wait for it to end.</summary>
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _jobCts;
    /// <summary>UTC ticks of the last busy change (read and written on several threads, so a long).</summary>
    private long _lastBusyTicks;
    private (long ConversationId, long AssistantId)? _currentConversation;
    /// <summary>Update memory runs: its jobs go on although the chat is blocked (<see cref="MemoryVault.IsLearning"/>).</summary>
    private volatile bool _updating;
    private Task? _loop;
    private volatile bool _paused;
    private int _finishing;

    public MemoryWorker(MemoryVault vault, IVaultIndex index, IVaultStorage storage, IAssistantStore assistants,
        IAssistantActivity activity, AssistantSession session, AssistantContext context, ILanguageModel llm,
        IOptions<LocalAiOptions> options, TimeProvider time, ILogger<MemoryWorker> logger, WorkActivity? work = null)
    {
        _work = work ?? new WorkActivity();
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
        _lastBusyTicks = time.GetUtcNow().UtcTicks;
        _activity.BusyChanged += OnBusyChanged;
        _session.UserMessageAdded += OnUserMessage;
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

    /// <summary>The user turned the automatic memory update on or off (Settings › Memory).</summary>
    public void AutoUpdateChanged() => Wake();

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

    /// <summary>
    /// Queues the identity note of every assistant (written only where missing or out of date): after an assistant was
    /// created or got a new shortcut.
    /// </summary>
    public async Task QueueIdentityNotesAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var a in await _assistants.ListAsync(ct).ConfigureAwait(false))
                await _index.EnqueueAsync(MemoryJobKind.WriteIdentity, a.Id, null, null, _time.GetUtcNow(), ct).ConfigureAwait(false);
            Wake();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not queue the identity notes"); }
    }

    /// <summary>
    /// Does all the memory work left for <paramref name="assistantId"/> now, without waiting for idle time: the notes
    /// of its conversations, then its profile, its identity note and the index. Used before its AI is turned off
    /// (the language model writes the notes) and before the app exits. Cancelling stops the running job and leaves
    /// the rest queued. Returns the number of jobs that ran.
    /// </summary>
    /// <summary>
    /// Update memory (the sidebar button): all the memory work left for the assistant now, like <see cref="FinishAsync"/>,
    /// with the chat blocked meanwhile and each step reported. Notes already up to date are skipped. Needs its AI on.
    /// </summary>
    public async Task<int> UpdateMemoryAsync(long assistantId, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (_llm.State != LanguageModelState.Ready) throw new InvalidOperationException("Turn the Brain on to update the memory.");
        using var work = _vault.TryBeginExclusiveWork() ?? throw new InvalidOperationException("The assistant is already learning.");
        _updating = true;
        try
        {
            var ran = await FinishAsync(assistantId, ct, progress).ConfigureAwait(false);
            _logger.LogInformation("Update memory of assistant {Id}: {Jobs} jobs", assistantId, ran);
            return ran;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>What a job does, as the Update memory status shows it.</summary>
    private static string Describe(MemoryJobKind kind) => kind switch
    {
        MemoryJobKind.ConsolidateProfile => "Updating the profile",
        MemoryJobKind.WriteIdentity => "Updating the identity note",
        MemoryJobKind.IndexNote => "Indexing the notes",
        _ => "Moving the old memory",
    };

    public async Task<int> FinishAsync(long assistantId, CancellationToken ct) => await FinishAsync(assistantId, ct, null).ConfigureAwait(false);

    private async Task<int> FinishAsync(long assistantId, CancellationToken ct, IProgress<string>? progress)
    {
        Interlocked.Increment(ref _finishing);
        CancelJob(); // a job of the idle loop gives way
        try
        {
            await _running.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = _time.GetUtcNow();
                // Notes whose file was deleted leave the index first: their conversations then need a note again.
                await _vault.ForgetMissingNotesAsync(assistantId, ct).ConfigureAwait(false);
                // Like the idle loop, only conversations since notes exist (the first run), but quiet or not.
                var since = await _index.GetStateAsync(NotesSinceKey, ct).ConfigureAwait(false) is { } stored &&
                            DateTimeOffset.TryParse(stored, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : now;
                var conversations = await _index.ConversationsNeedingNotesAsync(since, now, ct).ConfigureAwait(false);
                foreach (var (conversationId, _) in conversations.Where(c => c.AssistantId == assistantId))
                    await _index.EnqueueAsync(MemoryJobKind.SummarizeConversation, assistantId, conversationId, null, now, ct).ConfigureAwait(false);
                if (_currentConversation is { } current && current.AssistantId == assistantId)
                    await _index.EnqueueAsync(MemoryJobKind.SummarizeConversation, assistantId, current.ConversationId, null, now, ct).ConfigureAwait(false);
                await _index.EnqueueAsync(MemoryJobKind.ConsolidateProfile, assistantId, null, null, now.AddMilliseconds(1), ct).ConfigureAwait(false);
                await _index.EnqueueAsync(MemoryJobKind.WriteIdentity, assistantId, null, null, now.AddMilliseconds(2), ct).ConfigureAwait(false);

                var ran = 0;
                // The conversations without an up-to-date note; the current one is checked too (skipped when up to date).
                var stale = conversations.Where(c => c.AssistantId == assistantId).Select(c => c.ConversationId).ToHashSet();
                var summary = 0;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
                // Jobs that still wait (e.g. postponed after a failure) stay queued: only the ones due now run.
                while (await _index.NextJobAsync(now.AddMilliseconds(2), ct, assistantId).ConfigureAwait(false) is { } job)
                {
                    var step = job.Kind != MemoryJobKind.SummarizeConversation ? Describe(job.Kind)
                        : job.ConversationId is { } id && stale.Contains(id) ? $"Summarizing conversation {++summary} of {stale.Count}"
                        : "Checking the current conversation";
                    progress?.Report(step);
                    await RunJobAsync(job, linked.Token).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    ran++;
                }
                return ran;
            }
            finally
            {
                _running.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _finishing);
            Wake();
        }
    }

    // ---------------- Triggers ----------------

    private void OnBusyChanged(object? sender, bool busy)
    {
        Interlocked.Exchange(ref _lastBusyTicks, _time.GetUtcNow().UtcTicks);
        if (busy)
        {
            // The user comes first: the job and the idle update stop (the rest stays queued).
            CancelJob();
            lock (_gate)
            {
                try { _idleUpdate?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
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
        try
        {
            await PrepareAsync(stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The memory could not start");
            return;
        }

        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Off (the default): nothing runs on its own; Update memory does the work. On: the same work, once
                // the assistant has been idle long enough, and again only after the user has been back.
                var busySince = Interlocked.Read(ref _lastBusyTicks);
                if (!_options.AutoUpdate || _paused || _finishing > 0 || _vault.IsLearning || busySince == _idleUpdatedAt
                    || !IsIdleLongEnough(_time.GetUtcNow()) || _llm.State != LanguageModelState.Ready || _context.Current is not { } assistant)
                {
                    await WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }
                await UpdateWhileIdleAsync(assistant.Id, busySince, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Memory work loop failed");
                await WaitAsync(stop).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Quiet since the last reply for <see cref="MemoryOptions.ConversationIdleMinutes"/> (the automatic update waits for it).</summary>
    private bool IsIdleLongEnough(DateTimeOffset now) =>
        !_activity.IsBusy && now.UtcTicks - Interlocked.Read(ref _lastBusyTicks) >=
        Math.Max(IdleDelay.Ticks, TimeSpan.FromMinutes(Math.Max(0, _options.ConversationIdleMinutes)).Ticks);

    /// <summary>The busy time the last idle update ran after: one update per idle period.</summary>
    private long _idleUpdatedAt = -1;
    private CancellationTokenSource? _idleUpdate;

    /// <summary>The automatic update: what Update memory does, given way as soon as the user is back.</summary>
    private async Task UpdateWhileIdleAsync(long assistantId, long busySince, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_gate) _idleUpdate = cts;
        try
        {
            var jobs = await FinishAsync(assistantId, cts.Token).ConfigureAwait(false);
            _idleUpdatedAt = busySince;
            if (jobs > 0) _logger.LogInformation("Updated the memory of assistant {Id} while idle: {Jobs} jobs", assistantId, jobs);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            // The user is back: the rest waits for the next idle period.
        }
        finally
        {
            lock (_gate) _idleUpdate = null;
        }
    }

    private async Task WaitAsync(CancellationToken stop)
    {
        try { await _wake.WaitAsync(PollInterval, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
    }

    /// <summary>
    /// First run: notes are written only for conversations from now on. Every run: the old memory moves into the
    /// profiles once, each assistant's identity note is brought up to date, and its notes are checked against the
    /// index (edits made while the app was closed).
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
        foreach (var a in all)
        {
            await _index.EnqueueAsync(MemoryJobKind.WriteIdentity, a.Id, null, null, now, stop).ConfigureAwait(false);
            await _index.EnqueueAsync(MemoryJobKind.IndexNote, a.Id, null, null, now, stop).ConfigureAwait(false);
        }
        return since;
    }

    private async Task RunJobAsync(MemoryJob job, CancellationToken stop)
    {
        // Notes are written by the language model and indexed with the embedding model, which runs only with the AI:
        // indexed without it, the passages would have no embeddings.
        var needsAi = job.Kind is MemoryJobKind.SummarizeConversation or MemoryJobKind.ConsolidateProfile or MemoryJobKind.IndexNote;
        // While Learn runs, the memory work waits: it would race Learn for the embedding model.
        if ((needsAi && _llm.State != LanguageModelState.Ready) || (_vault.IsLearning && !_updating))
        {
            await _index.PostponeJobAsync(job.Id, _time.GetUtcNow() + TimeSpan.FromSeconds(30), failed: false, stop).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_gate) _jobCts = cts;
        _work.MemoryOwner = (await _assistants.ListAsync(stop).ConfigureAwait(false)).FirstOrDefault(a => a.Id == job.AssistantId)?.Name;
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
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            // Pre-empted by the user (or the update was stopped), the app still running: back in the queue, not a failure.
            await _index.PostponeJobAsync(job.Id, _time.GetUtcNow(), failed: false, _stop.Token).ConfigureAwait(false);
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
            _work.End(WorkKind.Memory);
            _work.MemoryOwner = null;
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
            case MemoryJobKind.WriteIdentity:
                await _vault.WriteIdentityNoteAsync(job.AssistantId, ct).ConfigureAwait(false);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _activity.BusyChanged -= OnBusyChanged;
        _session.UserMessageAdded -= OnUserMessage;
        _storage.Changed -= OnNoteChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
