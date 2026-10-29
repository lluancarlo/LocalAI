using LocalAI.Core.Desktop;

namespace LocalAI.Infrastructure;

/// <summary>Used on platforms without global shortcut support yet (the Win32 implementation is Windows-only).</summary>
internal sealed class NullGlobalHotkeys : IGlobalHotkeys
{
    public IDisposable Register(HotkeyGesture gesture, Action pressed) =>
        throw new HotkeyUnavailableException(gesture, "Global shortcuts are not supported on this platform.");
}
