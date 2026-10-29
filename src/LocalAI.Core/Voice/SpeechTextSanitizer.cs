using System.Text.RegularExpressions;

namespace LocalAI.Core.Voice;

/// <summary>Removes markdown and symbols that a TTS engine would read literally.</summary>
public static partial class SpeechTextSanitizer
{
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"`([^`]*)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"^\s{0,3}(#{1,6}\s+|[-*+]\s+|\d+[.)]\s+|>\s*)", RegexOptions.Multiline)] private static partial Regex LinePrefix();
    [GeneratedRegex(@"(\*\*|__|\*|~~)")] private static partial Regex Emphasis();
    [GeneratedRegex(@"https?://\S+")] private static partial Regex Url();
    [GeneratedRegex(@"[|#<>{}\[\]\\^]")] private static partial Regex Symbols();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    // Emoji and pictographs: not speakable.
    [GeneratedRegex(@"[☀-➿]|\uD83C[\uDC00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDC00-\uDFFF]")] private static partial Regex Emoji();

    public static string Clean(string text)
    {
        text = Link().Replace(text, "$1");
        text = InlineCode().Replace(text, "$1");
        text = LinePrefix().Replace(text, "");
        text = Emphasis().Replace(text, "");
        text = Url().Replace(text, "");
        text = Emoji().Replace(text, "");
        text = Symbols().Replace(text, " ");
        return Whitespace().Replace(text, " ").Trim();
    }
}
