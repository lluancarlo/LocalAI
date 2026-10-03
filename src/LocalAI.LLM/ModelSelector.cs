using LocalAI.Configuration;
using LocalAI.Core.Llm;

namespace LocalAI.LLM;

public sealed record ModelSelection(LlmCatalogEntry Entry, string FullPath, int ContextSize, int GpuLayers, int EstimatedVramMb);

/// <summary>
/// Chooses the LLM and its settings. "auto" picks the strongest installed catalog model whose VRAM requirement fits
/// the GPU (catalog order = preference order), so the user never has to reason about quantization or layers.
/// </summary>
public static class ModelSelector
{
    // Headroom kept free for Whisper, the embedding model, the desktop and the TTS runtime.
    private const int ComputeBufferMb = 600;

    public static ModelSelection Select(ModelCatalog catalog, LlmOptions options, string modelsDirectory, int? gpuTotalVramMb)
    {
        LlmCatalogEntry entry;
        string path;

        if (!string.IsNullOrWhiteSpace(options.ModelPath))
        {
            path = Path.GetFullPath(options.ModelPath, modelsDirectory);
            if (!File.Exists(path)) throw new ModelNotAvailableException($"Model not installed: {path}");
            entry = catalog.Llm.FirstOrDefault(e => SamePath(Path.Combine(modelsDirectory, e.File), path))
                    ?? new LlmCatalogEntry
                    {
                        Id = Path.GetFileNameWithoutExtension(path),
                        DisplayName = Path.GetFileNameWithoutExtension(path),
                        File = path,
                        Quantization = GuessQuantization(path),
                    };
        }
        else
        {
            var installed = catalog.Llm.Where(e => File.Exists(Path.Combine(modelsDirectory, e.File))).ToList();
            if (installed.Count == 0)
                throw new ModelNotAvailableException("Model not installed. Run scripts\\setup.ps1 to download a model.");

            if (!string.Equals(options.Model, "auto", StringComparison.OrdinalIgnoreCase))
            {
                entry = installed.FirstOrDefault(e => e.Id.Equals(options.Model, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ModelNotAvailableException(
                            $"Model '{options.Model}' is not installed. Installed: {string.Join(", ", installed.Select(e => e.Id))}");
            }
            else
            {
                var vram = gpuTotalVramMb ?? 0;
                entry = installed.FirstOrDefault(e => vram >= e.MinVramMb) ?? installed[^1];
            }
            path = Path.GetFullPath(Path.Combine(modelsDirectory, entry.File));
        }

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

    public static string GuessQuantization(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        string[] known = ["IQ1_S", "IQ1_M", "IQ2_XXS", "IQ2_XS", "IQ2_S", "IQ3_XXS", "IQ3_S", "IQ4_XS", "IQ4_NL",
            "Q2_K", "Q3_K_S", "Q3_K_M", "Q3_K_L", "Q4_0", "Q4_1", "Q4_K_S", "Q4_K_M", "Q4_K_XL", "Q5_0", "Q5_K_S",
            "Q5_K_M", "Q6_K", "Q8_0", "BF16", "F16", "F32"];
        return known.Where(q => name.Contains(q, StringComparison.Ordinal)).OrderByDescending(q => q.Length).FirstOrDefault() ?? "unknown";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
