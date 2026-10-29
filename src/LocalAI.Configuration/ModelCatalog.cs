using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Configuration;

/// <summary>
/// A language model: one of the catalog's (one file at <see cref="Url"/>), or one the user added from Hugging Face
/// (<see cref="AddedLanguageModels"/>: the <see cref="Files"/> of <see cref="Repo"/> downloaded into <see cref="Dir"/>).
/// </summary>
public sealed record LlmCatalogEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>The model file (the first part of a split model), relative to the models folder.</summary>
    public required string File { get; init; }
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Quantization { get; init; } = "";
    public double ParametersB { get; init; }
    public int MinVramMb { get; init; }
    public int DefaultContext { get; init; } = 8192;
    /// <summary>The longest context the model was trained for (0 = unknown): the most an assistant can choose.</summary>
    public int TrainedContext { get; init; }

    /// <summary>Added models: "owner/name" on Hugging Face.</summary>
    public string Repo { get; init; } = "";
    /// <summary>Added models: the commit the files were listed at.</summary>
    public string Revision { get; init; } = "";
    /// <summary>Added models: the repository files to download (several when the model is split).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>Added models: the folder the files go into, relative to the models folder.</summary>
    public string Dir { get; init; } = "";
    /// <summary>Added models: published by the company that made the model.</summary>
    public bool Official { get; init; }

    public bool IsAdded => Repo.Length > 0;
}

/// <summary>A company whose open models can be found on Hugging Face (Settings › Models).</summary>
public sealed record CompanyCatalogEntry
{
    public required string Name { get; init; }
    /// <summary>Hugging Face searches that find its models (GGUF repositories only).</summary>
    public IReadOnlyList<string> Search { get; init; } = [];
    /// <summary>Its own Hugging Face organizations: their repositories are marked official.</summary>
    public IReadOnlyList<string> Orgs { get; init; } = [];
}

public sealed record FileCatalogEntry
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = "";
    public required string File { get; init; }
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
}

/// <summary>
/// The speech model (Kokoro): one Hugging Face repository at a pinned revision, of which only <see cref="Files"/> are
/// downloaded (a name ending in "/" is a folder, with everything in it). Every voice is one of its speakers.
/// </summary>
public sealed record TtsCatalogEntry
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Dir { get; init; } = "";
    /// <summary>"owner/name" on Hugging Face.</summary>
    public string Repo { get; init; } = "";
    /// <summary>A commit, so the files (and the speaker ids in voices.bin) never change under the app.</summary>
    public string Revision { get; init; } = "main";
    public IReadOnlyList<string> Files { get; init; } = [];
    public long SizeBytes { get; init; }
}

public sealed record VoiceCatalogEntry
{
    public required string Id { get; init; }
    /// <summary>The speaker id in the model's voices.bin.</summary>
    public int Speaker { get; init; }
    /// <summary>ISO 639-1 code of the only language the voice speaks.</summary>
    public required string Language { get; init; }
    /// <summary>"pt-BR", "en-GB": shown in front of the voice's name.</summary>
    public string Locale { get; init; } = "";
    /// <summary>The espeak-ng language the model reads the text with ("pt-br", "it", "en-us", "en-gb").</summary>
    public string Lang { get; init; } = "";
    public string DisplayName { get; init; } = "";
    /// <summary>"female" or "male": picks the picture of an assistant created with this voice.</summary>
    public string? Gender { get; init; }
    public bool Default { get; init; }
    /// <summary>Changes to the voice settings for this voice.</summary>
    public VoiceSettingsOverride? Settings { get; init; }

    public VoiceTuning Tuning => VoiceTuning.Default.With(Settings);

    /// <summary>"[pt-BR] Dora (female)": the language first, so the voices of a language are easy to find.</summary>
    public string Label => Gender == null ? $"[{Locale}] {DisplayName}" : $"[{Locale}] {DisplayName} ({Gender})";
}

public sealed record RuntimeCatalogEntry
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Dir { get; init; } = "llama.cpp";
    public IReadOnlyList<string> Urls { get; init; } = [];
    public long SizeBytes { get; init; }
}

/// <summary>catalog.json (shipped next to the executable): everything the app can download and use.</summary>
public sealed record ModelCatalog
{
    public const string FileName = "catalog.json";

    public IReadOnlyList<LlmCatalogEntry> Llm { get; init; } = [];
    public IReadOnlyList<FileCatalogEntry> Embedding { get; init; } = [];
    public IReadOnlyList<FileCatalogEntry> Whisper { get; init; } = [];
    public IReadOnlyList<FileCatalogEntry> Vad { get; init; } = [];
    public TtsCatalogEntry Tts { get; init; } = new();
    public IReadOnlyList<VoiceCatalogEntry> Voices { get; init; } = [];
    public RuntimeCatalogEntry Runtime { get; init; } = new();
    public IReadOnlyList<CompanyCatalogEntry> Companies { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static ModelCatalog LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, FileName));

    public static ModelCatalog Load(string path) =>
        System.IO.File.Exists(path) ? Parse(System.IO.File.ReadAllText(path)) : new ModelCatalog();

    public static ModelCatalog Parse(string json) =>
        JsonSerializer.Deserialize<ModelCatalog>(json, JsonOptions) ?? new ModelCatalog();
}
