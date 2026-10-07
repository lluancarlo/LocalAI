using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Desktop;
using Microsoft.Extensions.Logging;

namespace LocalAI.Core.Voice;

public enum LiveState
{
    /// <summary>Nobody is listening.</summary>
    Off,
    /// <summary>A shortcut was pressed: switching assistant (possibly loading its language model) and opening the microphone.</summary>
    Starting,
    /// <summary>The live voice conversation is on (started by a shortcut or from the main window).</summary>
    Live,
}

/// <summary>
/// Turns a live voice conversation on and off from an assistant's global shortcut. Pressing it switches to that
/// assistant, opens a new conversation and starts listening; pressing it again stops. While one assistant is live, the
/// other assistants' shortcuts are ignored. <see cref="State"/> also follows live mode started from the main window.
/// </summary>
public sealed class LiveActivation : IDisposable
{
    private readonly AssistantHotkeys _hotkeys;
    private readonly AssistantManager _manager;
    private readonly AssistantContext _context;
    private readonly AssistantSession _session;
    private readonly ILiveVoice _voice;
    private readonly ILogger<LiveActivation> _logger;
    private readonly Lock _stateGate = new();
    private int _toggling;
    private AssistantProfile? _starting;

    public LiveActivation(AssistantHotkeys hotkeys, AssistantManager manager, AssistantContext context, AssistantSession session,
        ILiveVoice voice, ILogger<LiveActivation> logger)
    {
        _hotkeys = hotkeys;
        _manager = manager;
        _context = context;
        _session = session;
        _voice = voice;
        _logger = logger;
        _hotkeys.Pressed += OnHotkeyPressed;
        _voice.StateChanged += OnVoiceStateChanged;
    }

    public LiveState State { get; private set; } = LiveState.Off;

    /// <summary>The assistant that is starting or listening; null when off.</summary>
    public AssistantProfile? Assistant => State switch
    {
        LiveState.Off => null,
        LiveState.Starting => _starting ?? _context.Current,
        _ => _context.Current,
    };

    /// <summary>Raised (on any thread) when <see cref="State"/> changes.</summary>
    public event EventHandler<LiveState>? StateChanged;

    /// <summary>Raised when a shortcut could not start listening, with the reason.</summary>
    public event EventHandler<string>? Failed;

    private void OnHotkeyPressed(object? sender, long assistantId) => _ = ToggleAsync(assistantId);

    /// <summary>What pressing the assistant's shortcut does. Presses while a previous one is still being handled are ignored.</summary>
    public async Task ToggleAsync(long assistantId)
    {
        if (Interlocked.Exchange(ref _toggling, 1) == 1)
        {
            _logger.LogInformation("Shortcut of assistant {Id} ignored: the previous one is still being handled", assistantId);
            return;
        }
        try
        {
            if (_voice.IsContinuous)
            {
                if (_context.Current?.Id == assistantId)
                {
                    _logger.LogInformation("Shortcut: live conversation with assistant {Id} turned off", assistantId);
                    await _voice.StopContinuousAsync().ConfigureAwait(false);
                }
                else
                {
                    _logger.LogInformation("Shortcut of assistant {Id} ignored: assistant {Live} is live", assistantId, _context.Current?.Id);
                }
                return;
            }
            await StartAsync(assistantId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shortcut of assistant {Id} failed", assistantId);
            Failed?.Invoke(this, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _toggling, 0);
        }
    }

    private async Task StartAsync(long assistantId)
    {
        var profile = (await _manager.ListAsync().ConfigureAwait(false)).FirstOrDefault(a => a.Id == assistantId);
        if (profile == null) return;

        _starting = profile;
        SetState(LiveState.Starting);
        try
        {
            if (_context.Current?.Id != profile.Id) await _manager.SwitchAsync(profile).ConfigureAwait(false);
            _session.SelectConversation(null);
            await _voice.StartContinuousAsync().ConfigureAwait(false);
        }
        finally
        {
            _starting = null;
            SetState(_voice.IsContinuous ? LiveState.Live : LiveState.Off);
        }

        if (_voice.IsContinuous)
        {
            _logger.LogInformation("Shortcut: live conversation with assistant {Id} turned on", assistantId);
        }
        else
        {
            var reason = _voice.LastError ?? "The microphone could not be opened.";
            _logger.LogWarning("Shortcut of assistant {Id} could not start listening: {Reason}", assistantId, reason);
            Failed?.Invoke(this, reason);
        }
    }

    private void OnVoiceStateChanged(object? sender, VoiceState state)
    {
        if (State == LiveState.Starting) return; // StartAsync settles the state when it finishes
        SetState(_voice.IsContinuous ? LiveState.Live : LiveState.Off);
    }

    private void SetState(LiveState state)
    {
        lock (_stateGate)
        {
            if (State == state) return;
            State = state;
        }
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        _hotkeys.Pressed -= OnHotkeyPressed;
        _voice.StateChanged -= OnVoiceStateChanged;
    }
}
