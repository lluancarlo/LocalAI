namespace LocalAI.Core.Llm;

public enum ChatRole { System, User, Assistant }

public sealed record ChatMessage(ChatRole Role, string Content);

public sealed record GenerationOptions
{
    public float Temperature { get; init; } = 0.7f;
    public float TopP { get; init; } = 0.95f;
    public int MaxTokens { get; init; } = 1024;
    /// <summary>Hint for the engine to keep this request away from the interactive chat cache slot.</summary>
    public bool Background { get; init; }
}

/// <summary>Performance numbers for one generation, as reported by the engine plus client-side latency.</summary>
public sealed record GenerationStats
{
    public int PromptTokens { get; init; }
    public int CachedPromptTokens { get; init; }
    public double PromptMs { get; init; }
    public int GeneratedTokens { get; init; }
    public double TokensPerSecond { get; init; }
    public double TimeToFirstTokenMs { get; init; }
    public double TotalMs { get; init; }
}

/// <summary>A streamed piece of output. The final chunk may carry <see cref="Stats"/> and empty text.</summary>
public sealed record LlmChunk(string Text, GenerationStats? Stats = null);

public enum LanguageModelState { NotLoaded, Loading, Ready, Failed, Unloading }

public sealed record ModelInfo
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string FilePath { get; init; }
    public string Quantization { get; init; } = "";
    public int ContextSize { get; init; }
    /// <summary>-1 = all layers.</summary>
    public int GpuLayers { get; init; }
    public string Backend { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public int EstimatedVramMb { get; init; }
    /// <summary>VRAM attributed to the engine after load, when measurable.</summary>
    public int? MeasuredVramMb { get; init; }
    public TimeSpan LoadTime { get; init; }
}

/// <summary>Engine-agnostic chat model. Implementations: <c>LlamaCppLanguageModel</c>.</summary>
public interface ILanguageModel : IAsyncDisposable
{
    LanguageModelState State { get; }
    ModelInfo? Info { get; }
    string? LastError { get; }
    event EventHandler<LanguageModelState>? StateChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task UnloadAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages, GenerationOptions options, CancellationToken cancellationToken = default);
}

public static class LanguageModelExtensions
{
    public static async Task<string> GenerateAsync(
        this ILanguageModel model, IReadOnlyList<ChatMessage> messages, GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in model.StreamAsync(messages, options, cancellationToken).ConfigureAwait(false))
            sb.Append(chunk.Text);
        return sb.ToString();
    }
}

public sealed class ModelNotAvailableException(string message, Exception? inner = null) : Exception(message, inner);
