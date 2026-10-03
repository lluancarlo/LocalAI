namespace LocalAI.Core.Memory;

public sealed record MemoryItem
{
    public long Id { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public long? SourceConversationId { get; init; }
    public float[]? Embedding { get; init; }
    public string? EmbeddingModel { get; init; }
}

public sealed record ScoredMemory(MemoryItem Memory, float Score);

/// <summary>Persistent long-term memory (facts about the user).</summary>
public interface IMemoryStore
{
    Task<MemoryItem> AddAsync(MemoryItem item, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task UpdateEmbeddingAsync(long id, float[] embedding, string model, CancellationToken ct = default);
}

public interface IMemoryRetriever
{
    Task<IReadOnlyList<ScoredMemory>> RetrieveAsync(string query, int topK, float minScore, CancellationToken ct = default);
}

public enum EmbeddingPurpose { Query, Document }

public interface IEmbeddingService
{
    bool IsAvailable { get; }
    string ModelId { get; }
    Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default);
}

public static class VectorMath
{
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.IsEmpty) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
