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
///     jessica-identity.md
///     jessica-profile.md
///     conversations/2026-10-22-read-selection.md
///     learn/                 (documents the user gives the assistant to learn: .md and .txt)
/// </code>
/// </summary>
public static class VaultLayout
{
    /// <summary>The folder created in the vault (or in data\memory) that holds every assistant's notes.</summary>
    public const string RootFolder = "local-ai";
    public const string ConversationsFolder = "conversations";
    public const string NoteExtension = ".md";
    public const string LearnFolderName = "learn";
    /// <summary>The kinds of file the learn folder takes: Markdown and plain text.</summary>
    public static readonly IReadOnlyList<string> LearnExtensions = [".md", ".txt"];
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

    /// <summary>The assistant's learn folder: documents it learns only when the user clicks Learn.</summary>
    public static string LearnFolder(string assistantFolder) => $"{assistantFolder}/{LearnFolderName}";

    /// <summary>A path in an assistant's learn folder or its subfolders ("nova/learn/manual.md"): never indexed as a note.</summary>
    public static bool IsLearnPath(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 3 && parts[1] == LearnFolderName;
    }

    /// <summary>
    /// The title of a note's passages: a note's file name (what [[links]] point to), and for a learned document its
    /// path in the learn folder without the extension ("unity-md/Manual/class-Rigidbody"), since documents in
    /// different subfolders often share a name (index.md).
    /// </summary>
    public static string PassageTitle(string path)
    {
        if (!IsLearnPath(path)) return Path.GetFileNameWithoutExtension(path);
        var inLearn = string.Join('/', path.Split('/').Skip(2));
        var extension = Path.GetExtension(inLearn);
        return extension.Length > 0 ? inLearn[..^extension.Length] : inLearn;
    }

    public static string ProfilePath(string assistantFolder) =>
        $"{assistantFolder}/{assistantFolder}-profile{NoteExtension}";

    /// <summary>The note on who the assistant is: its personality, model, voice and memory.</summary>
    public static string IdentityPath(string assistantFolder) =>
        $"{assistantFolder}/{assistantFolder}-identity{NoteExtension}";

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
