namespace LocalAI.Core.Desktop;

/// <summary>System-wide keyboard shortcuts that work while another application has the focus.</summary>
public interface IGlobalHotkeys
{
    /// <summary>
    /// Registers <paramref name="gesture"/>; <paramref name="pressed"/> runs on a background thread each time it is
    /// pressed (holding the keys does not repeat it). Dispose the result to unregister.
    /// </summary>
    /// <exception cref="HotkeyUnavailableException">Another application (or Windows) already uses the combination.</exception>
    IDisposable Register(HotkeyGesture gesture, Action pressed);
}

public sealed class HotkeyUnavailableException(HotkeyGesture gesture, string message) : Exception(message)
{
    public HotkeyGesture Gesture { get; } = gesture;
}
