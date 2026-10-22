using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalAI.Configuration;
using LocalAI.Core.Assistant;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;

namespace LocalAI.Core.Memory.Vault;

/// <summary>What the language model returns for a conversation note.</summary>
public sealed record ConversationSummary(
    string Summary, IReadOnlyList<string> Decisions, IReadOnlyList<string> OpenItems, IReadOnlyList<string> Facts);

/// <summary>The profile's sections, in order.</summary>
public sealed record ProfileContent(
    IReadOnlyList<string> About, IReadOnlyList<string> Preferences, IReadOnlyList<string> Skills,
    IReadOnlyList<string> Projects, IReadOnlyList<string> Routine)
{
    public static readonly ProfileContent Empty = new([], [], [], [], []);

    public IEnumerable<string> AllItems => About.Concat(Preferences).Concat(Skills).Concat(Projects).Concat(Routine);
}

/// <summary>
/// The business rules of the notes: what the language model is asked (always JSON) and how its answer becomes
/// Markdown. The model never writes Markdown itself, so every note has the same structure.
/// </summary>
public static class NoteFormats
{
    /// <summary>Characters of conversation sent to the model for one note (the most recent ones).</summary>
    public const int MaxTranscriptChars = 12_000;
    private const int MaxReplyChars = 800;
    private const int MaxSelectionChars = 600;

    public static MemoryHeadings Headings(LanguageData languages, string language) =>
        languages.Languages.TryGetValue(language, out var profile) && profile.MemoryHeadings is { } h ? h : new MemoryHeadings();

    /// <summary>The section titles of every language, so notes written before the notes language changed still read right.</summary>
    public static IReadOnlyList<MemoryHeadings> AllHeadings(LanguageData languages) =>
        [new MemoryHeadings(), .. languages.Languages.Values.Select(l => l.MemoryHeadings).OfType<MemoryHeadings>()];

    private static string LanguageRule(string language)
    {
        var name = PromptBuilder.LanguageName(language);
        return $"Write every string in {(name.Length > 0 ? name : "English")}. Keep names, code, file names, commands and " +
               "quoted text exactly as written, in their original language.";
    }

    // ---------------- Conversation notes ----------------

    public static IReadOnlyList<ChatMessage> ConversationPrompt(string assistantName, string language, IReadOnlyList<StoredMessage> messages)
    {
        var system =
            $"You keep the memory notes of {assistantName}, a personal assistant. Read the conversation between the USER " +
            "and the assistant and output ONLY a JSON object:\n" +
            "{\"summary\": \"...\", \"decisions\": [\"...\"], \"openItems\": [\"...\"], \"facts\": [\"...\"]}\n" +
            "- summary: 2 to 5 sentences on what was discussed and concluded.\n" +
            "- decisions: choices the user made or agreed to.\n" +
            "- openItems: things left to do or to follow up.\n" +
            "- facts: durable facts about the USER worth remembering in future conversations (preferences, personal " +
            "details, skills, projects, routine), each one short sentence about the user. Not one-off requests, not " +
            "general knowledge, not facts about the assistant.\n" +
            "Use empty arrays when nothing applies. " + LanguageRule(language);
        return [new(ChatRole.System, system), new(ChatRole.User, Transcript(messages) + "\n\nJSON object:")];
    }

    /// <summary>The conversation as plain text, newest part kept when it is too long.</summary>
    internal static string Transcript(IReadOnlyList<StoredMessage> messages)
    {
        var parts = new List<string>();
        var length = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m.Role == ChatRole.System || string.IsNullOrWhiteSpace(m.Content)) continue;
            var text = m.Role == ChatRole.Assistant && m.Content.Length > MaxReplyChars ? m.Content[..MaxReplyChars] + "…" : m.Content;
            if (m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.SelectedText))
            {
                var selection = m.SelectedText.Length > MaxSelectionChars ? m.SelectedText[..MaxSelectionChars] + "…" : m.SelectedText;
                text = $"(selected on screen: \"{selection}\")\n{text}";
            }
            var part = $"{(m.Role == ChatRole.User ? "USER" : "ASSISTANT")}: {text}";
            if (length + part.Length > MaxTranscriptChars && parts.Count > 0) break;
            parts.Add(part);
            length += part.Length;
        }
        parts.Reverse();
        return string.Join("\n\n", parts);
    }

    public static ConversationSummary? ParseConversation(string raw) =>
        NoteSections.ParseObject(raw) is not { } obj ? null : new ConversationSummary(
            NoteSections.GetString(obj, "summary"),
            NoteSections.GetStrings(obj, "decisions"),
            NoteSections.GetStrings(obj, "openItems"),
            NoteSections.GetStrings(obj, "facts"));

    public static string RenderConversation(ConversationSummary summary, MemoryHeadings h) => NoteSections.Render(
    [
        (h.Summary, summary.Summary, []),
        (h.Decisions, null, summary.Decisions),
        (h.OpenItems, null, summary.OpenItems),
        (h.FactsLearned, null, summary.Facts),
    ]);

    /// <summary>The facts listed in a conversation note's generated block (including the user's edits there).</summary>
    public static IReadOnlyList<string> FactsOf(string? block, IReadOnlyList<MemoryHeadings> headings) =>
        block == null ? [] : NoteSections.Parse(block).FirstOrDefault(s => headings.Any(h => s.Heading == h.FactsLearned))?.Items ?? [];

    // ---------------- Profile ----------------

    public static IReadOnlyList<ChatMessage> ProfilePrompt(string language, ProfileContent current,
        IReadOnlyCollection<string> userItems, IReadOnlyList<string> newFacts)
    {
        var system =
            "You maintain the profile of the USER kept by a personal assistant. Merge the current profile with the new " +
            "facts and output ONLY a JSON object:\n" +
            "{\"about\": [\"...\"], \"preferences\": [\"...\"], \"skills\": [\"...\"], \"projects\": [\"...\"], \"routine\": [\"...\"]}\n" +
            "Each item is one short sentence about the user. Merge duplicates. When a new fact contradicts an old one, " +
            "keep the new one. Items marked (user) were written by the user: keep them exactly, without the mark. " +
            "At most 15 items per section. " + LanguageRule(language);
        var sb = new StringBuilder("Current profile:\n");
        void Section(string name, IReadOnlyList<string> items)
        {
            sb.Append(name).Append(":\n");
            foreach (var item in items) sb.Append("- ").Append(userItems.Contains(item) ? "(user) " : "").Append(item).Append('\n');
        }
        Section("about", current.About);
        Section("preferences", current.Preferences);
        Section("skills", current.Skills);
        Section("projects", current.Projects);
        Section("routine", current.Routine);
        sb.Append("\nNew facts:\n");
        foreach (var fact in newFacts) sb.Append("- ").Append(fact).Append('\n');
        sb.Append("\nJSON object:");
        return [new(ChatRole.System, system), new(ChatRole.User, sb.ToString())];
    }

    public static ProfileContent? ParseProfile(string raw) =>
        NoteSections.ParseObject(raw) is not { } obj ? null : FromJson(obj);

    private static ProfileContent FromJson(JsonElement obj) => new(
        NoteSections.GetStrings(obj, "about"), NoteSections.GetStrings(obj, "preferences"), NoteSections.GetStrings(obj, "skills"),
        NoteSections.GetStrings(obj, "projects"), NoteSections.GetStrings(obj, "routine"));

    /// <summary>The profile as written in the note's generated block. Unknown sections count as "about".</summary>
    public static ProfileContent ReadProfile(string? block, IReadOnlyList<MemoryHeadings> headings)
    {
        if (string.IsNullOrWhiteSpace(block)) return ProfileContent.Empty;
        var about = new List<string>();
        var preferences = new List<string>();
        var skills = new List<string>();
        var projects = new List<string>();
        var routine = new List<string>();
        foreach (var section in NoteSections.Parse(block))
        {
            bool Is(Func<MemoryHeadings, string> title) => headings.Any(h => section.Heading == title(h));
            var target = Is(h => h.Preferences) ? preferences
                : Is(h => h.Skills) ? skills
                : Is(h => h.Projects) ? projects
                : Is(h => h.Routine) ? routine
                : about;
            target.AddRange(section.Items);
        }
        return new ProfileContent(about, preferences, skills, projects, routine);
    }

    /// <summary>
    /// Makes sure every item the user wrote survives the model's merge: a missing one goes back to the section it was in.
    /// </summary>
    public static ProfileContent KeepUserItems(ProfileContent merged, ProfileContent current, IReadOnlyCollection<string> userItems)
    {
        var present = merged.AllItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> Keep(IReadOnlyList<string> mergedItems, IReadOnlyList<string> currentItems) =>
            [.. mergedItems, .. currentItems.Where(i => userItems.Contains(i) && !present.Contains(i))];
        return new ProfileContent(
            Keep(merged.About, current.About), Keep(merged.Preferences, current.Preferences), Keep(merged.Skills, current.Skills),
            Keep(merged.Projects, current.Projects), Keep(merged.Routine, current.Routine));
    }

    /// <summary>Adds facts to "about" without the model (used to bring the old memory into the profile).</summary>
    public static ProfileContent AddFacts(ProfileContent profile, IEnumerable<string> facts)
    {
        var present = profile.AllItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var about = profile.About.ToList();
        foreach (var fact in facts.Select(f => f.Trim()).Where(f => f.Length > 0))
            if (present.Add(fact)) about.Add(fact);
        return profile with { About = about };
    }

    // ---------------- Identity ----------------

    /// <summary>
    /// The identity note's generated block: who the assistant is, its personality (the style the user wrote at
    /// creation) and where its memory is. Model, voice and settings go in the frontmatter (<see cref="IdentityProperties"/>).
    /// </summary>
    public static string RenderIdentity(AssistantProfile assistant, string profileTitle, MemoryHeadings h)
    {
        string Fill(string text) => text.Replace("{name}", assistant.Name, StringComparison.Ordinal)
            .Replace("{profile}", profileTitle, StringComparison.Ordinal);
        var personality = string.IsNullOrWhiteSpace(assistant.StylePrompt)
            ? Fill(h.NoPersonality)
            : assistant.StylePrompt.ReplaceLineEndings("\n").Trim();
        return NoteSections.Render(
        [
            (Fill(h.WhoIs), Fill(h.IdentityIntro), []),
            (h.Personality, personality, []),
            (h.Memory, Fill(h.IdentityMemory), []),
        ]);
    }

    /// <summary>The identity note's frontmatter (Obsidian shows it as the note's properties).</summary>
    public static IReadOnlyList<(string Key, string Value)> IdentityProperties(AssistantProfile assistant)
    {
        var s = assistant.VoiceStyle;
        string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        return
        [
            ("type", "identity"),
            ("assistant", NoteDocument.Quote(assistant.Name)),
            ("model", NoteDocument.Quote(assistant.ModelId)),
            ("voice", NoteDocument.Quote(assistant.VoiceId)),
            ("voice-language", NoteDocument.Quote(assistant.Language)),
            ("voice-speed", Number(s.Speed)),
            ("voice-pitch", Number(s.Pitch)),
            ("voice-expressiveness", Number(s.Expressiveness)),
            ("voice-rhythm", Number(s.Rhythm)),
            ("shortcut", NoteDocument.Quote(assistant.Hotkey?.ToString() ?? "")),
            ("created", assistant.CreatedAt.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)),
        ];
    }

    public static string RenderProfile(ProfileContent p, MemoryHeadings h) => NoteSections.Render(
    [
        (h.About, null, p.About),
        (h.Preferences, null, p.Preferences),
        (h.Skills, null, p.Skills),
        (h.Projects, null, p.Projects),
        (h.Routine, null, p.Routine),
    ]);
}
