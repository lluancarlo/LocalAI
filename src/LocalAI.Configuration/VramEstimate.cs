namespace LocalAI.Configuration;

/// <summary>Where a model runs on this computer.</summary>
public enum ModelFit
{
    /// <summary>All of it in the GPU's memory.</summary>
    Gpu,
    /// <summary>Split between the GPU and the system memory: runs, slower.</summary>
    GpuAndCpu,
    /// <summary>No GPU: in the system memory.</summary>
    Cpu,
    /// <summary>Larger than the GPU and system memory together.</summary>
    TooLarge,
}

/// <summary>How much GPU memory a language model needs.</summary>
public static class VramEstimate
{
    /// <summary>Headroom kept free for Whisper, the embedding model, the desktop and the TTS runtime.</summary>
    public const int ComputeBufferMb = 600;

    /// <summary>KV cache per token when the model's attention is not known (~0.05 MB, mid-size models, 16-bit).</summary>
    private const double CoarseMbPerToken = 0.05;

    /// <summary>
    /// Coarse estimate: weights + KV cache (~0.05 MB/token for mid-size models with flash attention / SWA) + compute
    /// buffers. The measured value from the driver is shown alongside it once loaded.
    /// </summary>
    public static int Mb(long fileSizeBytes, int contextSize) =>
        (int)(fileSizeBytes / (1024 * 1024)) + (int)(contextSize * CoarseMbPerToken) + ComputeBufferMb;

    /// <summary>
    /// Weights + KV cache + compute buffers, the cache from the model's own attention (<paramref name="metadata"/>) when
    /// known; <paramref name="compact"/> = the 8-bit cache.
    /// </summary>
    public static int Mb(long fileSizeBytes, int contextSize, GgufMetadata? metadata, bool compact) =>
        (int)(fileSizeBytes / (1024 * 1024)) + CacheMb(contextSize, metadata, compact) + ComputeBufferMb;

    /// <summary>The KV cache of <paramref name="contextSize"/> tokens, in MB.</summary>
    public static int CacheMb(int contextSize, GgufMetadata? metadata, bool compact) =>
        metadata is { FullAttentionLayers: > 0, KvHeads: > 0, KeyLength: > 0 }
            ? (int)((metadata.KvBytesPerToken(compact) * contextSize + metadata.SlidingWindowBytes(compact)) / (1024 * 1024))
            : (int)(contextSize * CoarseMbPerToken * (compact ? 0.53 : 1));
}
