namespace LocalAI.Configuration;

/// <summary>A context size the user can choose, with what it costs on this computer.</summary>
/// <param name="CacheMb">The KV cache it takes.</param>
/// <param name="TotalMb">Model, cache and buffers, plus the other engines on the GPU (hearing, memory search).</param>
public sealed record ContextOption(int Size, ModelFit Fit, int CacheMb, int TotalMb);

/// <summary>
/// The context sizes a model can use on this computer: from 4K up to what it was trained for, each with where it runs
/// (the GPU, split with the system memory, or not at all), from the model's own attention when its file is known.
/// </summary>
public static class ContextPlanner
{
    public const int MinContext = 4096;
    /// <summary>When the model does not say what it was trained for.</summary>
    public const int UnknownTrainedContext = 32768;
    /// <summary>Memory search (the embedding model) and hearing (Whisper) share the GPU with the language model.</summary>
    public const int OtherEnginesMb = 1500;

    public static readonly IReadOnlyList<int> Steps = [4096, 8192, 16384, 32768, 65536, 131072, 262144];

    public static IReadOnlyList<ContextOption> Options(long weightsBytes, int trainedContext, GgufMetadata? metadata, bool compact,
        int? gpuVramMb, long systemMemoryMb)
    {
        var max = metadata?.ContextLength is > 0 and var known ? known : trainedContext > 0 ? trainedContext : UnknownTrainedContext;
        return Steps.Where(s => s <= Math.Max(max, MinContext))
            .Select(size =>
            {
                var total = VramEstimate.Mb(weightsBytes, size, metadata, compact) + OtherEnginesMb;
                return new ContextOption(size, Fit(total, gpuVramMb, systemMemoryMb), VramEstimate.CacheMb(size, metadata, compact), total);
            })
            .ToList();
    }

    public static ModelFit Fit(int neededMb, int? gpuVramMb, long systemMemoryMb)
    {
        if (gpuVramMb is > 0 && neededMb <= gpuVramMb) return ModelFit.Gpu;
        // The weights and cache go wherever there is room, but the system needs some memory too.
        if (neededMb > (gpuVramMb ?? 0) + systemMemoryMb * 3 / 4) return ModelFit.TooLarge;
        return gpuVramMb is > 0 ? ModelFit.GpuAndCpu : ModelFit.Cpu;
    }

    /// <summary>"16K", "128K".</summary>
    public static string Label(int size) => size >= 1024 ? $"{size / 1024}K" : size.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
