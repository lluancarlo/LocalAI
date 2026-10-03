using System.Text.RegularExpressions;
using LocalAI.Core.Memory;
using Microsoft.Extensions.Logging;

namespace LocalAI.Memory;

/// <summary>
/// Semantic retrieval: cosine similarity between the query embedding and stored memory embeddings (brute force —
/// fine for thousands of memories). Falls back to keyword overlap when the embedding model is unavailable.
/// </summary>
public sealed partial class MemoryRetriever(IMemoryStore store, IEmbeddingService embeddings, ILogger<MemoryRetriever> logger)
    : IMemoryRetriever
{
    [GeneratedRegex(@"\p{L}{3,}")] private static partial Regex Word();

    public async Task<IReadOnlyList<ScoredMemory>> RetrieveAsync(string query, int topK, float minScore, CancellationToken ct = default)
    {
        var memories = await store.ListAsync(ct).ConfigureAwait(false);
        if (memories.Count == 0 || topK <= 0) return [];

        if (embeddings.IsAvailable)
        {
            try
            {
                var q = await embeddings.EmbedAsync(query, EmbeddingPurpose.Query, ct).ConfigureAwait(false);
                return memories
                    .Where(m => m.Embedding != null && m.EmbeddingModel == embeddings.ModelId)
                    .Select(m => new ScoredMemory(m, VectorMath.Cosine(q, m.Embedding)))
                    .Where(s => s.Score >= minScore)
                    .OrderByDescending(s => s.Score)
                    .Take(topK)
                    .ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Semantic retrieval failed; using keyword matching");
            }
        }
        return KeywordRetrieve(query, memories, topK);
    }

    internal static IReadOnlyList<ScoredMemory> KeywordRetrieve(string query, IReadOnlyList<MemoryItem> memories, int topK)
    {
        var q = Words(query);
        if (q.Count == 0) return [];
        return memories
            .Select(m =>
            {
                var w = Words(m.Content);
                var overlap = q.Count(w.Contains);
                return new ScoredMemory(m, overlap / (float)q.Count);
            })
            .Where(s => s.Score > 0)
            .OrderByDescending(s => s.Score)
            .Take(topK)
            .ToList();
    }

    private static HashSet<string> Words(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToHashSet();
}
