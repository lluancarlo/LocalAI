using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAI.Configuration;

public sealed record LlmCatalogEntry
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string File { get; init; }
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Quantization { get; init; } = "";
    public double ParametersB { get; init; }
    public int MinVramMb { get; init; }
    public int DefaultContext { get; init; } = 8192;
}

public sealed record FileCatalogEntry
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = "";
    public required string File { get; init; }
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
}

public sealed record VoiceCatalogEntry
{
    public required string Id { get; init; }
    public required string Language { get; init; }
    public string DisplayName { get; init; } = "";
    public required string Dir { get; init; }
    public string Url { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool Default { get; init; }
    /// <summary>The synthesis engine; it decides which voice settings exist (<see cref="VoiceTuning.ForEngine"/>).</summary>
    public string Engine { get; init; } = VoiceTuning.PiperEngine;
    /// <summary>Changes to the engine's settings for this voice, e.g. the values in its .onnx.json.</summary>
    public VoiceSettingsOverride? Settings { get; init; }

    public VoiceTuning Tuning => VoiceTuning.ForEngine(Engine).With(Settings);
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
    public IReadOnlyList<VoiceCatalogEntry> Tts { get; init; } = [];
    public RuntimeCatalogEntry Runtime { get; init; } = new();

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
