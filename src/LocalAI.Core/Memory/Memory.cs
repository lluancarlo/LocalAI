namespace LocalAI.Core.Memory;

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
