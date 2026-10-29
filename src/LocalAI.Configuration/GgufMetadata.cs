using System.Text;

namespace LocalAI.Configuration;

/// <summary>
/// What a GGUF model file says about its attention, read from its header only (no weights): the context it was
/// trained for and what one token of context costs in the KV cache. Read from the first file of a split model.
/// </summary>
public sealed record GgufMetadata(string Architecture, int ContextLength, int Layers, int FullAttentionLayers, int KvHeads,
    int KeyLength, int ValueLength, int SlidingWindow)
{
    /// <summary>
    /// Bytes of KV cache per token of context: keys and values of every layer that attends to the whole context.
    /// Layers with a sliding window (or none, in hybrid models) keep a fixed, small cache and are left out. The 8-bit
    /// cache (<paramref name="compact"/>) takes a little over half of the 16-bit one.
    /// </summary>
    public long KvBytesPerToken(bool compact) =>
        (long)FullAttentionLayers * KvHeads * (KeyLength + ValueLength) * (compact ? 17 : 32) / 16;

    /// <summary>The fixed cache of the sliding-window layers (they keep only their window of tokens).</summary>
    public long SlidingWindowBytes(bool compact) =>
        SlidingWindow <= 0 ? 0 : (long)(Layers - FullAttentionLayers) * KvHeads * (KeyLength + ValueLength) * SlidingWindow * (compact ? 17 : 32) / 16;

    /// <summary>The metadata of the GGUF file at <paramref name="path"/>, or null when it is missing or not readable.</summary>
    public static GgufMetadata? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    public static GgufMetadata Read(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != 0x46554747) throw new InvalidDataException("Not a GGUF file."); // "GGUF"
        var version = r.ReadUInt32();
        if (version < 2) throw new InvalidDataException($"GGUF version {version} is not supported.");
        r.ReadUInt64(); // tensors
        var count = r.ReadUInt64();

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            var key = ReadString(r);
            var type = r.ReadUInt32();
            // Only what the attention needs is kept; the rest (e.g. the tokenizer's vocabulary) is skipped.
            var keep = !key.StartsWith("tokenizer.", StringComparison.Ordinal);
            var value = ReadValue(r, type, keep);
            if (keep && value != null) values[key] = value;
        }

        var arch = values.GetValueOrDefault("general.architecture") as string ?? "";
        long Number(string name, long fallback = 0) => values.GetValueOrDefault($"{arch}.{name}") switch
        {
            long n => n,
            long[] array when array.Length > 0 => array.Max(),
            _ => fallback,
        };
        var layers = (int)Number("block_count");
        var heads = (int)Number("attention.head_count", 1);
        var kvHeads = (int)Number("attention.head_count_kv", heads);
        var headSize = heads > 0 ? (int)(Number("embedding_length") / heads) : 0;
        var window = (int)Number("attention.sliding_window");

        // Layers that attend to the whole context: all, unless the model says which ones do.
        var full = layers;
        if (values.GetValueOrDefault($"{arch}.attention.sliding_window_pattern") is bool[] pattern && pattern.Length > 0)
            full = pattern.Count(swa => !swa);
        else if (Number("attention.sliding_window_pattern") is > 1 and var every)
            full = (int)Math.Ceiling(layers / (double)every);
        else if (Number("full_attention_interval") is > 1 and var interval)
            full = (int)Math.Ceiling(layers / (double)interval);

        return new GgufMetadata(arch, (int)Number("context_length"), layers, Math.Clamp(full, 0, layers), kvHeads,
            (int)Number("attention.key_length", headSize), (int)Number("attention.value_length", headSize), window);
    }

    private static string ReadString(BinaryReader r)
    {
        var length = r.ReadUInt64();
        if (length > 1 << 24) throw new InvalidDataException("A GGUF string is too long.");
        return Encoding.UTF8.GetString(r.ReadBytes((int)length));
    }

    /// <summary>One value: numbers as long, booleans as bool, strings, arrays of numbers or booleans; null when skipped.</summary>
    private static object? ReadValue(BinaryReader r, uint type, bool keep)
    {
        switch (type)
        {
            case 0: return (long)r.ReadByte();
            case 1: return (long)r.ReadSByte();
            case 2: return (long)r.ReadUInt16();
            case 3: return (long)r.ReadInt16();
            case 4: return (long)r.ReadUInt32();
            case 5: return (long)r.ReadInt32();
            case 6: return (long)r.ReadSingle();
            case 7: return r.ReadByte() != 0;
            case 8: return ReadString(r);
            case 10: return (long)r.ReadUInt64();
            case 11: return r.ReadInt64();
            case 12: return (long)r.ReadDouble();
            case 9:
            {
                var itemType = r.ReadUInt32();
                var length = r.ReadUInt64();
                if (itemType == 8)
                {
                    for (ulong i = 0; i < length; i++) r.BaseStream.Seek((long)r.ReadUInt64(), SeekOrigin.Current);
                    return null;
                }
                var size = itemType switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => throw new InvalidDataException("Unknown GGUF type.") };
                if (!keep || length > 4096)
                {
                    r.BaseStream.Seek((long)length * size, SeekOrigin.Current);
                    return null;
                }
                if (itemType == 7)
                {
                    var flags = new bool[length];
                    for (ulong i = 0; i < length; i++) flags[i] = r.ReadByte() != 0;
                    return flags;
                }
                var numbers = new long[length];
                for (ulong i = 0; i < length; i++) numbers[i] = (long)ReadValue(r, itemType, true)!;
                return numbers;
            }
            default:
                throw new InvalidDataException($"Unknown GGUF type {type}.");
        }
    }
}
