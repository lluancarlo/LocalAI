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
