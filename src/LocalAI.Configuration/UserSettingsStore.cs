using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalAI.Configuration;

/// <summary>
/// Persists user-changed settings (device selection, voice mode, ...) to usersettings.json in the data directory.
/// That file is layered over appsettings.json at startup, so values written here win on next launch.
/// </summary>
public sealed class UserSettingsStore(string path)
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public string FilePath { get; } = path;

    /// <summary>Sets LocalAI:{section}:{key} = value and saves atomically.</summary>
    public void Set(string section, string key, object? value)
    {
        lock (_gate)
        {
            var root = Load();
            var localAi = GetOrAdd(root, LocalAiOptions.SectionName);
            var sec = GetOrAdd(localAi, section);
            sec[key] = value is null ? null : JsonSerializer.SerializeToNode(value);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    private static JsonObject GetOrAdd(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private JsonObject Load()
    {
        if (!File.Exists(FilePath)) return new JsonObject();
        try { return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}
