using LocalAI.Configuration;
using LocalAI.Core.Llm;

namespace LocalAI.LLM;

/// <param name="FitToMemory">The model needs more VRAM than the GPU has: it is split between GPU and CPU (slower).</param>
public sealed record ModelSelection(LlmCatalogEntry Entry, string FullPath, int ContextSize, int GpuLayers, int EstimatedVramMb,
    bool FitToMemory = false);

/// <summary>
/// Chooses the LLM and its settings: the active assistant's model, or with "auto" the strongest installed catalog
/// model whose VRAM requirement fits the GPU (catalog order = preference order; models added from Hugging Face come
/// last).
/// </summary>
public static class ModelSelector
{
    public const string NoModelMessage = "No language model installed. Download one in Settings > Models.";
    private const string AutoModel = "auto";

    public static ModelSelection Select(ModelCatalog catalog, LlmOptions options, string modelsDirectory, int? gpuTotalVramMb)
    {
        var installed = catalog.Llm.Where(e => File.Exists(Path.Combine(modelsDirectory, e.File))).ToList();
        LlmCatalogEntry entry;
        if (options.Model.Equals(AutoModel, StringComparison.OrdinalIgnoreCase))
        {
            if (installed.Count == 0) throw new ModelNotAvailableException(NoModelMessage);
            var vram = gpuTotalVramMb ?? 0;
            entry = installed.FirstOrDefault(e => vram >= e.MinVramMb) ?? installed[^1];
        }
        else
        {
            var chosen = catalog.Llm.FirstOrDefault(e => e.Id.Equals(options.Model, StringComparison.OrdinalIgnoreCase))
                         ?? throw new ModelNotAvailableException($"Unknown language model '{options.Model}'.");
            entry = installed.Contains(chosen)
                ? chosen
                : throw new ModelNotAvailableException($"{chosen.DisplayName} is not installed yet. Download it in Settings > Models.");
        }
        var path = Path.GetFullPath(Path.Combine(modelsDirectory, entry.File));

        var context = options.ContextSize > 0 ? options.ContextSize : entry.DefaultContext;
        var gpuLayers = gpuTotalVramMb is null or 0 ? 0 : options.GpuLayers;
        // A split model's first file is only part of it.
        var fileSize = Math.Max(new FileInfo(path).Length, entry.SizeBytes);
        // The model's default context and cache: the catalog's figure. Another one: from the model's own attention.
        var custom = options.ContextSize > 0 || options.CompactCache;
        var estimate = custom ? VramEstimate.Mb(fileSize, context, GgufMetadata.TryRead(path), options.CompactCache) : VramEstimate.Mb(fileSize, context);
        var needed = custom ? estimate + ContextPlanner.OtherEnginesMb : entry.MinVramMb;
        var fit = gpuLayers < 0 && gpuTotalVramMb is > 0 && needed > gpuTotalVramMb;
        return new ModelSelection(entry, path, context, gpuLayers, gpuLayers == 0 ? 0 : estimate, fit);
    }

    /// <summary>The VRAM the model takes: none on the CPU, else <see cref="VramEstimate.Mb"/>.</summary>
    public static int EstimateVramMb(long fileSizeBytes, int contextSize, int gpuLayers) =>
        gpuLayers == 0 ? 0 : VramEstimate.Mb(fileSizeBytes, contextSize);
}
