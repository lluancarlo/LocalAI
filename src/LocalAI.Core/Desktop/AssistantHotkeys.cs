using LocalAI.Core.Assistants;
using Microsoft.Extensions.Logging;

namespace LocalAI.Core.Desktop;

/// <summary>
/// Keeps one global shortcut registered per assistant that has one, re-registering whenever assistants change.
/// A shortcut another application already uses is reported in <see cref="Problems"/> instead of failing.
/// </summary>
public sealed class AssistantHotkeys : IDisposable
{
    private readonly IGlobalHotkeys _hotkeys;
    private readonly AssistantManager _manager;
    private readonly ILogger<AssistantHotkeys> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly List<IDisposable> _registrations = [];
    private IReadOnlyDictionary<long, string> _problems = new Dictionary<long, string>();
    private bool _started;
    private bool _suspended;
    private bool _disposed;

    public AssistantHotkeys(IGlobalHotkeys hotkeys, AssistantManager manager, ILogger<AssistantHotkeys> logger)
    {
        _hotkeys = hotkeys;
        _manager = manager;
        _logger = logger;
        _manager.AssistantsChanged += OnAssistantsChanged;
    }

    /// <summary>Raised on a background thread with the id of the assistant whose shortcut was pressed.</summary>
    public event EventHandler<long>? Pressed;

    /// <summary>Raised when <see cref="Problems"/> changes.</summary>
    public event EventHandler? ProblemsChanged;

    /// <summary>Why an assistant's shortcut could not be registered, by assistant id.</summary>
    public IReadOnlyDictionary<long, string> Problems => Volatile.Read(ref _problems);

    /// <summary>Registers the shortcuts. Until then (while the app starts) no shortcut does anything.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        _started = true;
        return SyncAsync(ct);
    }

    /// <summary>Releases every shortcut, e.g. while the user records a new one (so the keys reach the app window).</summary>
    public Task SuspendAsync()
    {
        _suspended = true;
        return SyncAsync(CancellationToken.None);
    }

    public Task ResumeAsync()
    {
        _suspended = false;
        return SyncAsync(CancellationToken.None);
    }

    private void OnAssistantsChanged(object? sender, EventArgs e) => _ = SyncSafelyAsync();

    private async Task SyncSafelyAsync()
    {
        try { await SyncAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Could not update the assistant shortcuts"); }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            UnregisterAll();
            if (!_started || _suspended || _disposed) return;

            var problems = new Dictionary<long, string>();
            foreach (var assistant in await _manager.ListAsync(ct).ConfigureAwait(false))
            {
                if (assistant.Hotkey is not { } gesture) continue;
                var id = assistant.Id;
                try
                {
                    _registrations.Add(_hotkeys.Register(gesture, () => Pressed?.Invoke(this, id)));
                }
                catch (HotkeyUnavailableException ex)
                {
                    _logger.LogWarning("Shortcut {Hotkey} of assistant {Id} is not available: {Reason}", gesture, id, ex.Message);
                    problems[id] = ex.Message;
                }
            }
            _logger.LogInformation("{Count} assistant shortcut(s) registered", _registrations.Count);
            var changed = problems.Count != Problems.Count || problems.Any(p => !Problems.TryGetValue(p.Key, out var old) || old != p.Value);
            Volatile.Write(ref _problems, problems);
            if (changed) ProblemsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _sync.Release();
        }
    }

    private void UnregisterAll()
    {
        foreach (var registration in _registrations) registration.Dispose();
        _registrations.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.AssistantsChanged -= OnAssistantsChanged;
        _sync.Wait();
        try { UnregisterAll(); }
        finally { _sync.Release(); }
    }
}
