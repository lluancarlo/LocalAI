using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAI.Configuration;
using LocalAI.Core.Diagnostics;
using LocalAI.Core.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.LLM;

/// <summary>
/// <see cref="ILanguageModel"/> backed by llama.cpp's llama-server (CUDA build) running as a local child process.
/// Streaming uses the server's OpenAI-compatible SSE endpoint over loopback only.
/// </summary>
public sealed class LlamaCppLanguageModel : ILanguageModel
{
    private readonly LocalAiOptions _options;
    private readonly LocalAiPaths _paths;
    private readonly ModelCatalog _catalog;
    private readonly IGpuInfoProvider _gpu;
    private readonly ILogger<LlamaCppLanguageModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _http = new(new LoopbackOnlyHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private LlamaServerProcess? _server;

    public LlamaCppLanguageModel(
        IOptions<LocalAiOptions> options, LocalAiPaths paths, ModelCatalog catalog, IGpuInfoProvider gpu,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _paths = paths;
        _catalog = catalog;
        _gpu = gpu;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LlamaCppLanguageModel>();
    }

    public LanguageModelState State { get; private set; } = LanguageModelState.NotLoaded;
    public ModelInfo? Info { get; private set; }
    public string? LastError { get; private set; }
    public event EventHandler<LanguageModelState>? StateChanged;

    private string ServerExe => Path.Combine(_paths.LlamaCppDirectory, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == LanguageModelState.Ready) return;
            SetState(LanguageModelState.Loading);
            LastError = null;

            var gpu = await _gpu.GetGpuInfoAsync(cancellationToken).ConfigureAwait(false);
            var selection = ModelSelector.Select(_catalog, _options.Llm, _paths.ModelsDirectory, gpu?.TotalMemoryMb);
            _logger.LogInformation("Selected model {Model} ({Quant}), ctx {Ctx}, GPU layers {Layers}, est. VRAM {Vram} MB (GPU: {Gpu})",
                selection.Entry.Id, selection.Entry.Quantization, selection.ContextSize, selection.GpuLayers,
                selection.EstimatedVramMb, gpu?.Name ?? "none");

            var sw = Stopwatch.StartNew();
            var usedBefore = gpu?.UsedMemoryMb;
            var backend = selection.GpuLayers == 0 ? "CPU" : "CUDA";
            var gpuLayers = selection.GpuLayers;
            try
            {
                _server = await StartServerAsync(selection, gpuLayers, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && gpuLayers != 0 && _options.Llm.CpuFallback)
            {
                _logger.LogError(ex, "GPU initialization failed; retrying on CPU");
                LastError = $"GPU initialization failed ({FirstLine(ex.Message)}); running on CPU (slower).";
                backend = "CPU (fallback)";
                gpuLayers = 0;
                _server = await StartServerAsync(selection, 0, cancellationToken).ConfigureAwait(false);
            }
            sw.Stop();

            int? measured = null;
            if (gpuLayers != 0)
            {
                measured = await _gpu.GetProcessMemoryMbAsync(_server.ProcessId, cancellationToken).ConfigureAwait(false);
                if (measured is null && usedBefore is not null)
                {
                    var after = await _gpu.GetGpuInfoAsync(cancellationToken).ConfigureAwait(false);
                    if (after != null) measured = Math.Max(0, after.UsedMemoryMb - usedBefore.Value);
                }
            }

            Info = new ModelInfo
            {
                Id = selection.Entry.Id,
                DisplayName = selection.Entry.DisplayName,
                FilePath = selection.FullPath,
                Quantization = selection.Entry.Quantization,
                ContextSize = selection.ContextSize,
                GpuLayers = gpuLayers,
                Backend = backend,
                FileSizeBytes = new FileInfo(selection.FullPath).Length,
                EstimatedVramMb = gpuLayers == 0 ? 0 : selection.EstimatedVramMb,
                MeasuredVramMb = measured,
                LoadTime = sw.Elapsed,
            };
            _logger.LogInformation("Model loaded in {Seconds:F2}s on {Backend}; measured VRAM {Vram} MB",
                sw.Elapsed.TotalSeconds, backend, measured);
            SetState(LanguageModelState.Ready);
        }
        catch (OperationCanceledException)
        {
            SetState(LanguageModelState.NotLoaded);
            throw;
        }
        catch (ModelNotAvailableException ex)
        {
            LastError = ex.Message;
            _logger.LogWarning("{Error}", LastError);
            SetState(LanguageModelState.Failed);
        }
        catch (Exception ex)
        {
            LastError = $"Failed to load model: {FirstLine(ex.Message)}";
            _logger.LogError(ex, "Model load failed");
            SetState(LanguageModelState.Failed);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Task<LlamaServerProcess> StartServerAsync(ModelSelection selection, int gpuLayers, CancellationToken ct) =>
        LlamaServerProcess.StartAsync(new LlamaServerStartInfo
        {
            Name = "llama-server (chat)",
            ExecutablePath = ServerExe,
            ModelPath = selection.FullPath,
            ContextSize = selection.ContextSize,
            GpuLayers = gpuLayers,
            FlashAttention = _options.Llm.FlashAttention,
            ParallelSlots = _options.Llm.ParallelSlots,
            StartupTimeout = TimeSpan.FromSeconds(_options.Llm.StartupTimeoutSeconds),
        }, _http, _loggerFactory.CreateLogger<LlamaServerProcess>(), ct);

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_server == null) return;
            SetState(LanguageModelState.Unloading);
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
            Info = null;
            _logger.LogInformation("Model unloaded");
            SetState(LanguageModelState.NotLoaded);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async IAsyncEnumerable<LlmChunk> StreamAsync(
        IReadOnlyList<ChatMessage> messages, GenerationOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var server = _server;
        if (server == null || State != LanguageModelState.Ready)
            throw new ModelNotAvailableException(LastError ?? "Model not loaded.");
        if (server.HasExited)
        {
            SetState(LanguageModelState.Failed);
            LastError = "The inference engine stopped unexpectedly.";
            throw new ModelNotAvailableException(LastError);
        }

        var body = BuildRequestBody(messages, options, _options.Llm.ParallelSlots);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "v1/chat/completions"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var sw = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Inference request failed ({(int)response.StatusCode}): {err}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        double? ttft = null;
        // Disposing the response when the consumer cancels closes the connection; llama-server then stops generating.
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var evt = SseParser.Parse(line);
            if (evt is null) continue;
            if (evt.Done) yield break;
            if (evt.Text is { Length: > 0 } text)
            {
                ttft ??= sw.Elapsed.TotalMilliseconds;
                yield return new LlmChunk(text);
            }
            if (evt.Stats is { } stats)
                yield return new LlmChunk("", stats with { TimeToFirstTokenMs = ttft ?? 0, TotalMs = sw.Elapsed.TotalMilliseconds });
        }
    }

    internal static string BuildRequestBody(IReadOnlyList<ChatMessage> messages, GenerationOptions options, int parallelSlots)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            msgs.Add(new JsonObject
            {
                ["role"] = m.Role switch { ChatRole.System => "system", ChatRole.Assistant => "assistant", _ => "user" },
                ["content"] = m.Content,
            });
        }
        var body = new JsonObject
        {
            ["messages"] = msgs,
            ["stream"] = true,
            ["temperature"] = options.Temperature,
            ["top_p"] = options.TopP,
            ["max_tokens"] = options.MaxTokens,
            ["cache_prompt"] = true,
        };
        // Pin interactive chat to slot 0 and background work to slot 1 so neither evicts the other's prompt cache.
        if (parallelSlots > 1) body["id_slot"] = options.Background ? 1 : 0;
        return body.ToJsonString();
    }

    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    private void SetState(LanguageModelState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync().ConfigureAwait(false);
        _server = null;
        _http.Dispose();
        _lifecycle.Dispose();
    }
}

/// <summary>Parses one SSE line of an OpenAI-compatible chat completion stream from llama-server.</summary>
internal static class SseParser
{
    internal sealed record SseEvent(string? Text, GenerationStats? Stats, bool Done);

    public static SseEvent? Parse(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line.AsSpan(5).Trim();
        if (payload.SequenceEqual("[DONE]")) return new SseEvent(null, null, true);

        using var doc = JsonDocument.Parse(payload.ToString());
        var root = doc.RootElement;

        string? text = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                text = content.GetString();
        }

        GenerationStats? stats = null;
        if (root.TryGetProperty("timings", out var t))
        {
            stats = new GenerationStats
            {
                PromptTokens = GetInt(t, "prompt_n"),
                CachedPromptTokens = GetInt(t, "cache_n"),
                PromptMs = GetDouble(t, "prompt_ms"),
                GeneratedTokens = GetInt(t, "predicted_n"),
                TokensPerSecond = GetDouble(t, "predicted_per_second"),
            };
        }
        return text is null && stats is null ? null : new SseEvent(text, stats, false);
    }

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static double GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
}
