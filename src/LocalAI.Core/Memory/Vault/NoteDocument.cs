using System.Security.Cryptography;
using System.Text;

namespace LocalAI.Core.Memory.Vault;

/// <summary>
/// A memory note: YAML frontmatter, then the body. The part the app writes sits between <see cref="BeginMarker"/> and
/// <see cref="EndMarker"/>; everything outside belongs to the user and is kept as it is. A note without the markers is
/// entirely the user's (<see cref="Generated"/> is null) and is never rewritten.
/// </summary>
public sealed record NoteDocument
{
    public const string BeginMarker = "<!-- local-ai:begin -->";
    public const string EndMarker = "<!-- local-ai:end -->";

    /// <summary>Frontmatter lines without the "---" delimiters; empty if the note has none.</summary>
    public string Frontmatter { get; init; } = "";
    /// <summary>Body before the generated block (the title, user text).</summary>
    public string Before { get; init; } = "";
    /// <summary>The generated block between the markers, or null if the markers are missing.</summary>
    public string? Generated { get; init; }
    /// <summary>Body after the generated block (user text).</summary>
    public string After { get; init; } = "";

    public bool HasGeneratedBlock => Generated != null;

    /// <summary>A new note: frontmatter, a level-1 title and the generated block, with room for the user below.</summary>
    public static NoteDocument Create(IEnumerable<(string Key, string Value)> frontmatter, string title, string generated) => new()
    {
        Frontmatter = string.Join('\n', frontmatter.Select(p => $"{p.Key}: {p.Value}")),
        Before = $"# {title}\n\n",
        Generated = generated,
        After = "\n",
    };

    public static NoteDocument Parse(string text)
    {
        text = text.ReplaceLineEndings("\n");
        var frontmatter = "";
        var body = text;
        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var close = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (close >= 0)
            {
                frontmatter = text[4..close];
                var rest = close + 4;
                if (rest < text.Length && text[rest] == '\n') rest++;
                body = text[rest..];
            }
        }

        var begin = body.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = begin < 0 ? -1 : body.IndexOf(EndMarker, begin + BeginMarker.Length, StringComparison.Ordinal);
        if (begin < 0 || end < 0) return new NoteDocument { Frontmatter = frontmatter, Before = body };

        return new NoteDocument
        {
            Frontmatter = frontmatter,
            Before = body[..begin],
            Generated = body[(begin + BeginMarker.Length)..end].Trim('\n'),
            After = body[(end + EndMarker.Length)..],
        };
    }

    public string Render()
    {
        var sb = new StringBuilder();
        if (Frontmatter.Length > 0) sb.Append("---\n").Append(Frontmatter.TrimEnd('\n')).Append("\n---\n");
        sb.Append(Before);
        if (Generated != null)
        {
            sb.Append(BeginMarker).Append('\n').Append(Generated.Trim('\n')).Append('\n').Append(EndMarker);
            sb.Append(After);
        }
        return sb.ToString();
    }

    /// <summary>The note with frontmatter <paramref name="key"/> set (replaced if present, else added).</summary>
    public NoteDocument WithFrontmatter(string key, string value)
    {
        var lines = Frontmatter.Length == 0 ? new List<string>() : Frontmatter.Split('\n').ToList();
        var line = $"{key}: {value}";
        var index = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        if (index >= 0) lines[index] = line;
        else lines.Add(line);
        return this with { Frontmatter = string.Join('\n', lines) };
    }

    /// <summary>The value of frontmatter <paramref name="key"/> (unquoted), or null.</summary>
    public string? GetFrontmatter(string key)
    {
        foreach (var line in Frontmatter.Split('\n'))
        {
            if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
            var value = line[(key.Length + 1)..].Trim();
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\\\"", "\"") : value;
        }
        return null;
    }

    /// <summary>
    /// Fingerprint of a generated block, insensitive to line endings and trailing spaces, so an editor that only
    /// re-saves the file does not count as a user edit.
    /// </summary>
    public static string Hash(string? text)
    {
        if (text == null) return "";
        var normalized = string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).Trim();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    /// <summary>A YAML string in double quotes.</summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
