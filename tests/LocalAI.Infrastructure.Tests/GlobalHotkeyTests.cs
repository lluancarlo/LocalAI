using LocalAI.Core.Desktop;
using LocalAI.Desktop;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAI.Infrastructure.Tests;

public sealed class GlobalHotkeyTests
{
    [Fact]
    public void Every_supported_key_has_a_distinct_virtual_key_code()
    {
        var codes = HotkeyGesture.SupportedKeys.ToDictionary(k => k, Win32GlobalHotkeys.VirtualKey);
        Assert.Equal(codes.Count, codes.Values.Distinct().Count());
        Assert.Equal(0x44u, codes["D"]);
        Assert.Equal(0x30u, codes["0"]);
        Assert.Equal(0x67u, codes["NumPad7"]);
        Assert.Equal(0x70u, codes["F1"]);
        Assert.Equal(0x87u, codes["F24"]);
    }

    [Fact]
    public void Registers_unregisters_and_reports_a_combination_already_in_use()
    {
        if (!OperatingSystem.IsWindows()) return;
        // An unusual combination no other application is expected to use. Nothing is pressed: only registration is tested.
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F24", out var gesture));
        using var hotkeys = new Win32GlobalHotkeys(NullLogger<Win32GlobalHotkeys>.Instance);

        var registration = hotkeys.Register(gesture, () => { });
        var error = Assert.Throws<HotkeyUnavailableException>(() => hotkeys.Register(gesture, () => { }));
        Assert.Equal(gesture, error.Gesture);
        Assert.Contains("already used", error.Message, StringComparison.Ordinal);

        registration.Dispose();
        registration.Dispose(); // idempotent
        using var again = hotkeys.Register(gesture, () => { });
    }

    [Fact]
    public void Disposing_releases_every_shortcut()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(HotkeyGesture.TryParse("Ctrl+Alt+Shift+F23", out var gesture));
        var first = new Win32GlobalHotkeys(NullLogger<Win32GlobalHotkeys>.Instance);
        var registration = first.Register(gesture, () => { });
        first.Dispose();
        registration.Dispose(); // after the owner is gone: no error
        Assert.Throws<ObjectDisposedException>(() => first.Register(gesture, () => { }));

        using var second = new Win32GlobalHotkeys(NullLogger<Win32GlobalHotkeys>.Instance);
        using var taken = second.Register(gesture, () => { });
    }
}
