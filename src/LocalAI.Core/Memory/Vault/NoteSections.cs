using System.Text;
using System.Text.Json;

namespace LocalAI.Core.Memory.Vault;

/// <summary>A "## " section of a note: its title and text.</summary>
public sealed record NoteSection(string Heading, string Body)
{
    /// <summary>The section's list items ("- " or "* "); a paragraph counts as one item.</summary>
    public IReadOnlyList<string> Items => NoteSections.Items(Body);
}

/// <summary>Reads and writes the "## " sections of note bodies, and reads the JSON the language model returns.</summary>
public static class NoteSections
{
    public static IReadOnlyList<NoteSection> Parse(string markdown)
    {
        var sections = new List<NoteSection>();
        string? heading = null;
        var body = new StringBuilder();
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (heading != null || body.ToString().Trim().Length > 0) sections.Add(new NoteSection(heading ?? "", body.ToString().Trim()));
                heading = line[3..].Trim();
                body.Clear();
            }
            else body.Append(line).Append('\n');
        }
        if (heading != null || body.ToString().Trim().Length > 0) sections.Add(new NoteSection(heading ?? "", body.ToString().Trim()));
        return sections;
    }

    public static IReadOnlyList<string> Items(string body)
    {
        var items = new List<string>();
        var paragraph = new StringBuilder();
        void Flush()
        {
            if (paragraph.Length > 0) items.Add(paragraph.ToString().Trim());
            paragraph.Clear();
        }
        foreach (var raw in body.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                Flush();
                var item = line[2..].Trim();
                if (item.Length > 0) items.Add(item);
            }
            else if (line.Length == 0) Flush();
            else paragraph.Append(paragraph.Length > 0 ? " " : "").Append(line);
        }
        Flush();
        return items;
    }

    /// <summary>Sections as Markdown. A section with a paragraph shows it; one with items lists them; empty ones are left out.</summary>
    public static string Render(IEnumerable<(string Heading, string? Paragraph, IReadOnlyList<string> Items)> sections)
    {
        var sb = new StringBuilder();
        foreach (var (heading, paragraph, items) in sections)
        {
            if (string.IsNullOrWhiteSpace(paragraph) && items.Count == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("## ").Append(heading).Append('\n');
            if (!string.IsNullOrWhiteSpace(paragraph)) sb.Append(paragraph.Trim()).Append('\n');
            foreach (var item in items) sb.Append("- ").Append(item.ReplaceLineEndings(" ").Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The first JSON object in the model's output, or null.</summary>
    public static JsonElement? ParseObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()!.Trim() : "";

    /// <summary>A string array property, cleaned: trimmed, no empty or very long entries, no duplicates.</summary>
    public static IReadOnlyList<string> GetStrings(JsonElement obj, string name, int max = 15)
    {
        if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return [];
        return p.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.ReplaceLineEndings(" ").Trim())
            .Where(s => s.Length is > 2 and < 400)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }
}
