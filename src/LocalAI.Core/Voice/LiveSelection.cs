using LocalAI.Configuration;
using LocalAI.Core.Desktop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Voice;

/// <summary>
/// "Read selection": while live mode is on and the option is enabled, keeps the latest text the user selected with the
/// mouse in another application. The next message (spoken or typed) takes it as context; each selection is sent once.
/// The selection is never logged.
/// </summary>
public sealed class LiveSelection : IDisposable
{
    private readonly ITextSelectionMonitor _monitor;
    private readonly IOptionsMonitor<LocalAiOptions> _options;
    private readonly ILogger<LiveSelection> _logger;
    private readonly Lock _gate = new();
    private IDisposable? _watch;
    private bool _live;
    private string? _pending;

    public LiveSelection(ITextSelectionMonitor monitor, IOptionsMonitor<LocalAiOptions> options, ILogger<LiveSelection> logger)
    {
        _monitor = monitor;
        _options = options;
        _logger = logger;
    }

    public bool IsSupported => _monitor.IsSupported;

    /// <summary>True while selections are being watched (live mode on and the option enabled).</summary>
    public bool IsWatching
    {
        get { lock (_gate) return _watch != null; }
    }

    /// <summary>The selection waiting for the next message, if any.</summary>
    public string? Pending
    {
        get { lock (_gate) return _pending; }
    }

    /// <summary>Raised (on any thread) when <see cref="Pending"/> changes.</summary>
    public event EventHandler? PendingChanged;

    /// <summary>Live mode turned on or off. Turning it off forgets the pending selection.</summary>
    public void SetLive(bool live)
    {
        lock (_gate) _live = live;
        Refresh();
    }

    /// <summary>Applies a change of the Read selection option.</summary>
    public void Refresh()
    {
        bool cleared;
        lock (_gate)
        {
            var watch = _live && _options.CurrentValue.Voice.ReadSelection && _monitor.IsSupported;
            if (watch && _watch == null)
            {
                _watch = _monitor.Start(OnSelected);
                _logger.LogInformation("Watching text selections");
            }
            else if (!watch && _watch != null)
            {
                _watch.Dispose();
                _watch = null;
                _logger.LogInformation("Stopped watching text selections");
            }
            cleared = !watch && _pending != null;
            if (!watch) _pending = null;
        }
        if (cleared) PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the pending selection and forgets it, so it goes with one message only.</summary>
    public string? Take()
    {
        string? text;
        lock (_gate)
        {
            text = _pending;
            _pending = null;
        }
        if (text != null) PendingChanged?.Invoke(this, EventArgs.Empty);
        return text;
    }

    private void OnSelected(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var max = Math.Max(1, _options.CurrentValue.Voice.SelectionMaxChars);
        if (text.Length > max) text = text[..max].TrimEnd() + "…";
        lock (_gate)
        {
            if (_watch == null) return; // stopped while the selection was being read
            _pending = text;
        }
        _logger.LogInformation("Text selection captured ({Length} characters)", text.Length);
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _watch?.Dispose();
            _watch = null;
            _pending = null;
            _live = false;
        }
    }
}
