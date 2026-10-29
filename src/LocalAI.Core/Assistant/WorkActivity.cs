namespace LocalAI.Core.Assistant;

/// <summary>Who a step belongs to: the reply to the user, or the memory work in the background.</summary>
public enum WorkKind { Reply, Memory }

/// <summary>One step of the work, e.g. "Writing the conversation note: the model summarizes the conversation".</summary>
public sealed record WorkStep(WorkKind Kind, string Text);

/// <summary>
/// What the app is doing right now, step by step (the status bar shows it). The reply and the memory work each have
/// their own current step; the reply's comes first. Step texts never contain what the user said or wrote.
/// <see cref="SlowMotion"/> pauses at every step so the steps can be watched (Settings › Diagnostics).
/// </summary>
public sealed class WorkActivity
{
    private readonly Lock _gate = new();
    private WorkStep? _reply;
    private WorkStep? _memory;

    /// <summary>Pause after every step; zero (the default) means none.</summary>
    public TimeSpan SlowMotion { get; set; }

    /// <summary>Whose memory the background work is on (the assistant's name), shown before its steps.</summary>
    public string? MemoryOwner { get; set; }

    /// <summary>The reply's step if a reply is being made, else the memory work's, else null.</summary>
    public WorkStep? Current
    {
        get { lock (_gate) return _reply ?? _memory; }
    }

    /// <summary>Raised (on any thread) when <see cref="Current"/> may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Shows <paramref name="text"/> as the current step of <paramref name="kind"/>, then waits in slow motion.</summary>
    public async Task StepAsync(WorkKind kind, string text, CancellationToken ct = default)
    {
        if (kind == WorkKind.Memory && MemoryOwner is { Length: > 0 } owner) text = $"{owner} · {text}";
        Set(kind, new WorkStep(kind, text));
        var pause = SlowMotion;
        if (pause > TimeSpan.Zero) await Task.Delay(pause, ct).ConfigureAwait(false);
    }

    /// <summary>The work of <paramref name="kind"/> is over.</summary>
    public void End(WorkKind kind) => Set(kind, null);

    private void Set(WorkKind kind, WorkStep? step)
    {
        lock (_gate)
        {
            if (kind == WorkKind.Reply) _reply = step;
            else _memory = step;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
