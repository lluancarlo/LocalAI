namespace LocalAI.Core.Desktop;

/// <summary>Text the user selects with the mouse in other applications (drag, double-click or triple-click).</summary>
public interface ITextSelectionMonitor
{
    /// <summary>False where selections cannot be read (no implementation for this platform).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Starts watching. <paramref name="selected"/> runs on a background thread with the text of each new non-empty
    /// selection. Selections in this application are ignored. Dispose the result to stop.
    /// </summary>
    IDisposable Start(Action<string> selected);
}
