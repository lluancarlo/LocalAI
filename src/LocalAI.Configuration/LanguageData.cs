using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Configuration;

public sealed record LanguageProfile
{
    public string Name { get; init; } = "";
    /// <summary>Sentence spoken when previewing a voice of this language.</summary>
    public string VoiceSample { get; init; } = "";
    public IReadOnlyList<string> CommonWords { get; init; } = [];
    public IReadOnlyList<string> WordEndings { get; init; } = [];
    /// <summary>Letters that appear in this language and not in the other supported ones.</summary>
    public string Letters { get; init; } = "";
    public IReadOnlyList<string> LetterGroups { get; init; } = [];
    /// <summary>Section titles of the memory notes written in this language.</summary>
    public MemoryHeadings? MemoryHeadings { get; init; }
    /// <summary>Short sounds the assistant makes while the user talks, in Live mode ("mm-hmm").</summary>
    public IReadOnlyList<string> Reactions { get; init; } = [];
    /// <summary>Phrases that fill the wait when a spoken reply is slow to start ("hmm, let me see...").</summary>
    public IReadOnlyList<string> Fillers { get; init; } = [];
    /// <summary>Lower-case words a finished sentence rarely ends with (and, because, the...).</summary>
    public IReadOnlyList<string> UnfinishedEndings { get; init; } = [];
}

/// <summary>Section titles and fixed sentences of the memory notes (conversation notes, the profile, the identity).</summary>
public sealed record MemoryHeadings
{
    public string Summary { get; init; } = "Summary";
    public string Decisions { get; init; } = "Decisions";
    public string OpenItems { get; init; } = "Open items";
    public string FactsLearned { get; init; } = "Facts learned";
    public string About { get; init; } = "About";
    public string Preferences { get; init; } = "Preferences";
    public string Skills { get; init; } = "Skills";
    public string Projects { get; init; } = "Projects";
    public string Routine { get; init; } = "Routine";
    public string Profile { get; init; } = "Profile";

    // The identity note: who the assistant is. {name} is the assistant's name, {profile} the profile note's title.
    public string Identity { get; init; } = "Identity";
    public string WhoIs { get; init; } = "Who {name} is";
    public string Personality { get; init; } = "Personality and style";
    public string Memory { get; init; } = "Memory";
    public string IdentityIntro { get; init; } =
        "{name} is a personal AI that runs fully offline on this computer, in Local AI. {name} talks with the user by " +
        "text and by voice, replies in the language the user writes or speaks, always with the same voice, and " +
        "remembers the user through the notes in this folder.";
    public string NoPersonality { get; init; } = "No personality was defined at creation: {name} answers in a helpful, precise and concise way.";
    public string IdentityMemory { get; init; } =
        "What {name} knows about the user is in [[{profile}]]. Each conversation has its own note in the conversations folder.";
}

/// <summary>languages.json (shipped next to the executable): per-language text data, kept out of the code.</summary>
public sealed record LanguageData
{
    public const string FileName = "languages.json";

    /// <summary>Keyed by ISO 639-1 code.</summary>
    public IReadOnlyDictionary<string, LanguageProfile> Languages { get; init; } = new Dictionary<string, LanguageProfile>();
    /// <summary>Whisper output on silence or noise (lower case, matched as substrings).</summary>
    public IReadOnlyList<string> TranscriptNoise { get; init; } = [];
    /// <summary>Lower-case abbreviations whose period does not end a sentence.</summary>
    public IReadOnlyList<string> Abbreviations { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static LanguageData LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, FileName));

    public static LanguageData Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<LanguageData>(File.ReadAllText(path), JsonOptions) ?? new() : new();
}
