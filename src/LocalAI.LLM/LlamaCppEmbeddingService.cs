using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalAI.Configuration;
using LocalAI.Core.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LocalAI.LLM;

/// <summary>
/// Local text embeddings (EmbeddingGemma by default) served by a second llama-server instance in embedding mode.
/// Used for semantic long-term memory.
/// </summary>
public sealed class LlamaCppEmbeddingService : IEmbeddingService, IAsyncDisposable
{
    private readonly EmbeddingOptions _options;
    private readonly LocalAiPaths _paths;
    private readonly ModelCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly HttpClient _http = new(new LoopbackOnlyHandler()) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private LlamaServerProcess? _server;

    public LlamaCppEmbeddingService(IOptions<LocalAiOptions> options, LocalAiPaths paths, ModelCatalog catalog, ILoggerFactory loggerFactory)
    {
        _options = options.Value.Embedding;
        _paths = paths;
        _catalog = catalog;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LlamaCppEmbeddingService>();
    }

    public bool IsAvailable => _server is { HasExited: false };
    public string ModelId => _options.Model;
    public string? LastError { get; private set; }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.Enabled) { LastError = "Disabled in configuration."; return; }
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsAvailable) return;
            var entry = _catalog.Embedding.FirstOrDefault(e => e.Id == _options.Model);
            var path = entry != null ? Path.GetFullPath(Path.Combine(_paths.ModelsDirectory, entry.File)) : null;
            if (path == null || !File.Exists(path))
            {
                LastError = $"Embedding model '{_options.Model}' not installed; memory uses keyword matching.";
                _logger.LogWarning("{Error}", LastError);
                return;
            }
            _server = await LlamaServerProcess.StartAsync(new LlamaServerStartInfo
            {
                Name = "llama-server (embeddings)",
                ExecutablePath = Path.Combine(_paths.LlamaCppDirectory, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server"),
                ModelPath = path,
                ContextSize = 2048,
                GpuLayers = _options.GpuLayers,
                ParallelSlots = 1,
                Embedding = true,
                StartupTimeout = TimeSpan.FromSeconds(60),
            }, _http, _loggerFactory.CreateLogger<LlamaServerProcess>(), ct).ConfigureAwait(false);
            LastError = null;
            _logger.LogInformation("Embedding model {Model} ready", _options.Model);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastError = $"Embedding model failed to start: {ex.Message.Split('\n')[0]}";
            _logger.LogError(ex, "Embedding service failed to start");
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_server != null) await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
            LastError = $"Embedding model '{_options.Model}' not installed; memory uses keyword matching.";
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<float[]> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken ct = default)
    {
        var server = _server ?? throw new InvalidOperationException("Embedding service not started.");
        var prefix = purpose == EmbeddingPurpose.Query ? _options.QueryPrefix : _options.DocumentPrefix;
        var body = new JsonObject { ["input"] = prefix + text }.ToJsonString();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.BaseUri, "v1/embeddings"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var arr = doc.RootElement.GetProperty("data")[0].GetProperty("embedding");
        var vector = new float[arr.GetArrayLength()];
        var i = 0;
        foreach (var v in arr.EnumerateArray()) vector[i++] = v.GetSingle();
        return vector;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync().ConfigureAwait(false);
        _server = null;
        _http.Dispose();
        _startGate.Dispose();
    }
}
