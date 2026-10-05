using LocalAI.Configuration;
using LocalAI.Core.Llm;

namespace LocalAI.LLM;

public sealed record ModelSelection(LlmCatalogEntry Entry, string FullPath, int ContextSize, int GpuLayers, int EstimatedVramMb);

/// <summary>
/// Chooses the LLM and its settings: the active assistant's model, or with "auto" the strongest installed catalog
/// model whose VRAM requirement fits the GPU (catalog order = preference order).
/// </summary>
public static class ModelSelector
{
    public const string NoModelMessage = "No language model installed. Download one in Settings > Models.";
    private const string AutoModel = "auto";

    // Headroom kept free for Whisper, the embedding model, the desktop and the TTS runtime.
    private const int ComputeBufferMb = 600;

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
        var fileSize = new FileInfo(path).Length;
        return new ModelSelection(entry, path, context, gpuLayers, EstimateVramMb(fileSize, context, gpuLayers));
    }

    /// <summary>
    /// Coarse estimate: weights + KV cache (~0.05 MB/token for mid-size models with flash attention / SWA) + compute
    /// buffers. The measured value from the driver is shown alongside it once loaded.
    /// </summary>
    public static int EstimateVramMb(long fileSizeBytes, int contextSize, int gpuLayers)
    {
        if (gpuLayers == 0) return 0;
        return (int)(fileSizeBytes / (1024 * 1024)) + (int)(contextSize * 0.05) + ComputeBufferMb;
    }
}
