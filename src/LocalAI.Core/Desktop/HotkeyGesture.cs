using System.Diagnostics.CodeAnalysis;

namespace LocalAI.Core.Desktop;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A system-wide keyboard shortcut such as <c>Ctrl+Alt+D</c>: one or more modifiers and one key. The text form
/// (<see cref="ToString"/>, <see cref="TryParse"/>) is what is stored and shown; modifiers always come in the order
/// Ctrl, Alt, Shift, Win.
/// </summary>
public sealed record HotkeyGesture
{
    /// <summary>Push-to-talk inside the main window; it cannot also be a global shortcut.</summary>
    public static readonly HotkeyGesture PushToTalk = new(HotkeyModifiers.Ctrl, "Space");

    private static readonly HotkeyModifiers[] ModifierOrder = [HotkeyModifiers.Ctrl, HotkeyModifiers.Alt, HotkeyModifiers.Shift, HotkeyModifiers.Win];

    /// <summary>Keys a shortcut can end with, by their canonical name.</summary>
    public static IReadOnlySet<string> SupportedKeys { get; } = BuildSupportedKeys();

    private HotkeyGesture(HotkeyModifiers modifiers, string key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public HotkeyModifiers Modifiers { get; }

    /// <summary>Canonical key name: <c>A</c>–<c>Z</c>, <c>0</c>–<c>9</c>, <c>F1</c>–<c>F24</c>, <c>NumPad0</c>–<c>NumPad9</c>, <c>Space</c>, ...</summary>
    public string Key { get; }

    /// <summary>Creates a gesture, or explains why the combination cannot be a global shortcut.</summary>
    public static bool TryCreate(HotkeyModifiers modifiers, string key, [NotNullWhen(true)] out HotkeyGesture? gesture,
        [NotNullWhen(false)] out string? error)
    {
        gesture = null;
        var canonical = SupportedKeys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
        {
            error = $"{key} cannot be used in a shortcut.";
            return false;
        }
        if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            error = "A shortcut needs Ctrl, Alt or Win, so it does not get in the way of normal typing.";
            return false;
        }
        var candidate = new HotkeyGesture(modifiers, canonical);
        if (candidate == PushToTalk)
        {
            error = $"{PushToTalk} is push-to-talk in the main window.";
            return false;
        }
        gesture = candidate;
        error = null;
        return true;
    }

    /// <summary>Parses the stored form, e.g. <c>Ctrl+Alt+D</c> (case-insensitive, any modifier order).</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0)) return false;

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            if (!Enum.TryParse<HotkeyModifiers>(part, ignoreCase: true, out var modifier) || modifier == HotkeyModifiers.None ||
                !ModifierOrder.Contains(modifier) || modifiers.HasFlag(modifier))
                return false;
            modifiers |= modifier;
        }
        return TryCreate(modifiers, parts[^1], out gesture, out _);
    }

    public override string ToString() =>
        string.Join('+', ModifierOrder.Where(m => Modifiers.HasFlag(m)).Select(m => m.ToString()).Append(Key));

    private static HashSet<string> BuildSupportedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 'A'; c <= 'Z'; c++) keys.Add(c.ToString());
        for (var d = 0; d <= 9; d++)
        {
            keys.Add(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
            keys.Add($"NumPad{d}");
        }
        for (var f = 1; f <= 24; f++) keys.Add($"F{f}");
        foreach (var k in new[] { "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Pause", "Up", "Down", "Left", "Right" })
            keys.Add(k);
        return keys;
    }
}
