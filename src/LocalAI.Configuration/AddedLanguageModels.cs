using System.Text.Json;

namespace LocalAI.Configuration;

/// <summary>
/// The language models the user added from Hugging Face (Settings › Models), kept in
/// <see cref="LocalAiPaths.AddedLanguageModelsPath"/>. They join the catalog's models everywhere a model is listed or
/// loaded (<see cref="Merge"/>), after them, so the hardware recommendation stays the catalog's.
/// </summary>
public sealed class AddedLanguageModels(LocalAiPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public IReadOnlyList<LlmCatalogEntry> All
    {
        get
        {
            lock (_gate) return Read();
        }
    }

    /// <summary>Adds the model, or replaces the one with the same id.</summary>
    public void Add(LlmCatalogEntry entry)
    {
        lock (_gate)
        {
            var all = Read().Where(e => e.Id != entry.Id).Append(entry).ToList();
            Write(all);
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Any(e => e.Id == id)) Write(all.Where(e => e.Id != id).ToList());
        }
    }

    /// <summary>The catalog with the added models after its own.</summary>
    public ModelCatalog Merge(ModelCatalog catalog)
    {
        var added = All.Where(a => catalog.Llm.All(e => e.Id != a.Id)).ToList();
        return added.Count == 0 ? catalog : catalog with { Llm = [.. catalog.Llm, .. added] };
    }

    private List<LlmCatalogEntry> Read()
    {
        try
        {
            return File.Exists(paths.AddedLanguageModelsPath)
                ? JsonSerializer.Deserialize<List<LlmCatalogEntry>>(File.ReadAllText(paths.AddedLanguageModelsPath))?
                    .Where(e => e.IsAdded).ToList() ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(List<LlmCatalogEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AddedLanguageModelsPath)!);
        File.WriteAllText(paths.AddedLanguageModelsPath, JsonSerializer.Serialize(entries, JsonOptions));
    }
}
