namespace LocalAI.Core.Memory;

public enum EmbeddingPurpose { Query, Document }

public interface IEmbeddingService
{
    bool IsAvailable { get; }
    string ModelId { get; }
    Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default);

    /// <summary>Several texts in one request, in order (the default asks for them one by one).</summary>
    async Task<IReadOnlyList<float[]>> EmbedManyAsync(IReadOnlyList<string> texts, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        var vectors = new List<float[]>(texts.Count);
        foreach (var text in texts) vectors.Add(await EmbedAsync(text, purpose, ct).ConfigureAwait(false));
        return vectors;
    }
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

    /// <summary>
    /// The dot product, several numbers at a time (SIMD). Of two vectors of length 1 (<see cref="Normalize"/>) it is
    /// their cosine: recall compares the question with every passage this way.
    /// </summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) return 0;
        var sum = System.Numerics.Vector<float>.Zero;
        var width = System.Numerics.Vector<float>.Count;
        var i = 0;
        for (; i <= a.Length - width; i += width)
            sum += new System.Numerics.Vector<float>(a.Slice(i, width)) * new System.Numerics.Vector<float>(b.Slice(i, width));
        var dot = System.Numerics.Vector.Sum(sum);
        for (; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    /// <summary><paramref name="vector"/> scaled to length 1 (a copy); a vector of zeros stays as it is.</summary>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        var length = MathF.Sqrt(Dot(vector, vector));
        var result = vector.ToArray();
        if (length > 0)
            for (var i = 0; i < result.Length; i++) result[i] /= length;
        return result;
    }
}
