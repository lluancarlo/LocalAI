using Avalonia.Input;
using LocalAI.Core.Desktop;

namespace LocalAI.App.Views;

/// <summary>Turns a key press in the window into the parts of a <see cref="HotkeyGesture"/>.</summary>
internal static class HotkeyInput
{
    /// <summary>False while only modifiers are held (the user has not pressed the final key yet).</summary>
    public static bool TryRead(KeyEventArgs e, out HotkeyModifiers modifiers, out string key)
    {
        modifiers = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyModifiers.Win;

        key = e.Key switch
        {
            Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.None => "",
            >= Key.D0 and <= Key.D9 => ((int)(e.Key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Enum aliases: PageUp/Prior and PageDown/Next share values, so ToString() is ambiguous.
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            _ => e.Key.ToString(),
        };
        return key.Length > 0;
    }
}
