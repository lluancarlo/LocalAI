using System.Globalization;
using System.Text;
using LocalAI.Core.Assistants;

namespace LocalAI.Core.Memory.Vault;

/// <summary>
/// Where the memory notes live inside the vault. Paths are relative to the vault's <see cref="RootFolder"/>, use '/'
/// and are made of portable names: lower case, ASCII, words joined by '-'.
/// <code>
/// local-ai/
///   jessica/
///     jessica-profile.md
///     conversations/2026-10-22-read-selection.md
/// </code>
/// </summary>
public static class VaultLayout
{
    /// <summary>The folder created in the vault (or in data\memory) that holds every assistant's notes.</summary>
    public const string RootFolder = "local-ai";
    public const string ConversationsFolder = "conversations";
    public const string NoteExtension = ".md";
    private const int MaxSlugLength = 60;

    /// <summary>A portable file or folder name: lower case ASCII letters and digits, words joined by '-'.</summary>
    public static string Slug(string text, string fallback = "note")
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // accents
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(lower);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].TrimEnd('-');
        return slug.Length == 0 ? fallback : slug;
    }

    /// <summary>The assistant's folder. Two assistants whose names give the same slug are told apart by id.</summary>
    public static string AssistantFolder(AssistantProfile assistant, IEnumerable<AssistantProfile> all)
    {
        var slug = Slug(assistant.Name, "assistant");
        var shared = all.Any(a => a.Id != assistant.Id && Slug(a.Name, "assistant") == slug);
        return shared ? $"{slug}-{assistant.Id}" : slug;
    }

    public static string ProfilePath(string assistantFolder) =>
        $"{assistantFolder}/{assistantFolder}-profile{NoteExtension}";

    /// <summary>
    /// A new conversation note's path: date and title. <paramref name="exists"/> tells which paths are taken, so a
    /// second conversation with the same title on the same day gets "-2".
    /// </summary>
    public static string ConversationPath(string assistantFolder, DateTimeOffset date, string title, Func<string, bool> exists)
    {
        var stem = string.Create(CultureInfo.InvariantCulture,
            $"{assistantFolder}/{ConversationsFolder}/{date:yyyy-MM-dd}-{Slug(title, "conversation")}");
        var path = stem + NoteExtension;
        for (var n = 2; exists(path); n++) path = $"{stem}-{n}{NoteExtension}";
        return path;
    }
}
