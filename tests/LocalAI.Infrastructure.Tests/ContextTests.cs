using System.Text;
using LocalAI.Configuration;
using LocalAI.LLM;

namespace LocalAI.Infrastructure.Tests;

/// <summary>The context sizes a model can use: its GGUF attention, what each size costs, and how llama.cpp starts.</summary>
public sealed class ContextTests
{
    /// <summary>A GGUF header like Gemma's: 4 layers, 3 with a 512-token sliding window, and a tokenizer to skip.</summary>
    private static byte[] Gguf()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        void U32(string key, uint value) { Str(key); w.Write(4u); w.Write(value); }
        w.Write(0x46554747u); // "GGUF"
        w.Write(3u);
        w.Write(0ul); // tensors
        w.Write(10ul); // keys
        Str("general.architecture"); w.Write(8u); Str("gemma4");
        Str("tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write(3ul); Str("<s>"); Str("a"); Str("b");
        U32("gemma4.context_length", 131072);
        U32("gemma4.block_count", 4);
        U32("gemma4.embedding_length", 2048);
        U32("gemma4.attention.head_count", 8);
        U32("gemma4.attention.head_count_kv", 2);
        U32("gemma4.attention.key_length", 256);
        U32("gemma4.attention.sliding_window", 512);
        Str("gemma4.attention.sliding_window_pattern"); w.Write(9u); w.Write(7u); w.Write(4ul);
        w.Write((byte)1); w.Write((byte)1); w.Write((byte)1); w.Write((byte)0);
        return stream.ToArray();
    }

    [Fact]
    public void The_gguf_header_gives_the_trained_context_and_the_cache_per_token()
    {
        var m = GgufMetadata.Read(new MemoryStream(Gguf()));

        Assert.Equal(("gemma4", 131072, 4, 1), (m.Architecture, m.ContextLength, m.Layers, m.FullAttentionLayers));
        Assert.Equal((2, 256, 256), (m.KvHeads, m.KeyLength, m.ValueLength)); // value length falls back to the head size
        // One full-attention layer: 2 heads x (256 + 256) values x 2 bytes; the 8-bit cache a little over half.
        Assert.Equal(2048, m.KvBytesPerToken(compact: false));
        Assert.Equal(1088, m.KvBytesPerToken(compact: true));
        Assert.Equal(3L * 2 * 512 * 512 * 2, m.SlidingWindowBytes(compact: false));
    }

    [Fact]
    public void A_file_that_is_not_gguf_gives_nothing()
    {
        var path = Path.Combine(LocalAI.Tests.TestPaths.New("gguf"), "missing.gguf");
        Assert.Null(GgufMetadata.TryRead(path)); // missing
        Assert.Throws<InvalidDataException>(() => GgufMetadata.Read(new MemoryStream(Encoding.ASCII.GetBytes("nope nope nope nope"))));
    }

    [Fact]
    public void The_sizes_go_up_to_what_the_model_was_trained_for_and_say_where_they_run()
    {
        var weights = 5_000_000_000L;
        // An 8 GB GPU: the model (~4.8 GB), its buffers and the other engines leave room for about 16K of 16-bit cache.
        var options = ContextPlanner.Options(weights, trainedContext: 131072, metadata: null, compact: false, gpuVramMb: 8192, systemMemoryMb: 64000);

        Assert.Equal([4096, 8192, 16384, 32768, 65536, 131072], options.Select(o => o.Size));
        Assert.Equal(ModelFit.Gpu, options.First(o => o.Size == 16384).Fit);
        Assert.Equal(ModelFit.GpuAndCpu, options.First(o => o.Size == 131072).Fit); // ~6.5 GB of cache spills into the system memory
        // The compact cache takes about half: the same size fits where the 16-bit one did not.
        var compact = ContextPlanner.Options(weights, 131072, null, compact: true, gpuVramMb: 8192, systemMemoryMb: 64000);
        Assert.True(compact.First(o => o.Size == 65536).CacheMb < options.First(o => o.Size == 65536).CacheMb * 0.6);

        // Unknown training: up to 32K. No room at all: does not run.
        Assert.Equal(32768, ContextPlanner.Options(weights, 0, null, false, 16376, 64000).Max(o => o.Size));
        Assert.All(ContextPlanner.Options(weights, 8192, null, false, 2000, 2000), o => Assert.Equal(ModelFit.TooLarge, o.Fit));
    }

    [Fact]
    public void The_model_file_sizes_the_cache_when_it_is_known()
    {
        var m = GgufMetadata.Read(new MemoryStream(Gguf()));
        // 65536 tokens x 2048 bytes + the sliding windows: far less than the coarse 0.05 MB per token.
        Assert.Equal((int)((2048L * 65536 + m.SlidingWindowBytes(false)) / (1024 * 1024)), VramEstimate.CacheMb(65536, m, compact: false));
        Assert.True(VramEstimate.CacheMb(65536, m, false) < VramEstimate.CacheMb(65536, null, false));
    }

    [Fact]
    public void The_compact_cache_starts_llama_cpp_with_8_bit_keys_and_values()
    {
        var args = string.Join(' ', LlamaServerProcess.BuildArguments(new LlamaServerStartInfo
        {
            ExecutablePath = "x", ModelPath = "m.gguf", ContextSize = 65536, CompactCache = true,
        }, 1));
        Assert.Contains("--ctx-size 65536", args, StringComparison.Ordinal);
        Assert.Contains("--cache-type-k q8_0 --cache-type-v q8_0", args, StringComparison.Ordinal);
        Assert.DoesNotContain("--cache-type", string.Join(' ', LlamaServerProcess.BuildArguments(
            new LlamaServerStartInfo { ExecutablePath = "x", ModelPath = "m.gguf" }, 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_catalog_model_says_what_it_was_trained_for()
    {
        Assert.All(ModelCatalog.LoadDefault().Llm, e => Assert.True(e.TrainedContext >= e.DefaultContext, e.Id));
    }
}
