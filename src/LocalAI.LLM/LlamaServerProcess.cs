using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace LocalAI.LLM;

public sealed record LlamaServerStartInfo
{
    public required string ExecutablePath { get; init; }
    public required string ModelPath { get; init; }
    public int ContextSize { get; init; } = 8192;
    /// <summary>-1 = all layers, 0 = CPU only.</summary>
    public int GpuLayers { get; init; } = -1;
    public bool FlashAttention { get; init; } = true;
    public int ParallelSlots { get; init; } = 1;
    public bool Embedding { get; init; }
    public bool DisableReasoning { get; init; } = true;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public string Name { get; init; } = "llama-server";
}

/// <summary>
/// Supervises one llama.cpp <c>llama-server</c> child process bound to 127.0.0.1 on a random free port, protected by a
/// random API key passed through the environment (not visible on the command line). Network access is disabled
/// (<c>--offline</c>), the web UI is off, and the process is tied to the app's lifetime via a Job Object.
/// </summary>
public sealed class LlamaServerProcess : IAsyncDisposable
{
    private const int MaxLogLines = 200;
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly Queue<string> _recentLog = new();
    private readonly Lock _logGate = new();

    private LlamaServerProcess(Process process, Uri baseUri, string apiKey, ILogger logger)
    {
        _process = process;
        BaseUri = baseUri;
        ApiKey = apiKey;
        _logger = logger;
    }

    public Uri BaseUri { get; }
    public string ApiKey { get; }
    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    public string RecentLog
    {
        get { lock (_logGate) return string.Join(Environment.NewLine, _recentLog); }
    }

    public static async Task<LlamaServerProcess> StartAsync(
        LlamaServerStartInfo info, HttpClient http, ILogger logger, CancellationToken ct)
    {
        if (!File.Exists(info.ExecutablePath))
            throw new FileNotFoundException("llama.cpp runtime not installed (run scripts/setup.ps1).", info.ExecutablePath);
        if (!File.Exists(info.ModelPath))
            throw new FileNotFoundException("Model file not found.", info.ModelPath);

        var port = GetFreeLoopbackPort();
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        var psi = new ProcessStartInfo(info.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(info.ExecutablePath)!,
        };
        foreach (var arg in BuildArguments(info, port)) psi.ArgumentList.Add(arg);
        psi.Environment["LLAMA_API_KEY"] = apiKey;
        psi.Environment["LLAMA_ARG_OFFLINE"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var server = new LlamaServerProcess(process, new Uri($"http://127.0.0.1:{port}/"), apiKey, logger);
        process.ErrorDataReceived += (_, e) => server.OnLogLine(e.Data);
        process.OutputDataReceived += (_, e) => server.OnLogLine(e.Data);

        logger.LogInformation("Starting {Name}: {Args}", info.Name, string.Join(' ', psi.ArgumentList));
        if (!process.Start()) throw new InvalidOperationException("Failed to start llama-server.");
        ChildProcessJob.Assign(process);
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await server.WaitForHealthyAsync(http, info.StartupTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return server;
    }

    internal static IReadOnlyList<string> BuildArguments(LlamaServerStartInfo info, int port)
    {
        var args = new List<string>
        {
            "--model", info.ModelPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--ctx-size", info.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--n-gpu-layers", info.GpuLayers < 0 ? "999" : info.GpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--flash-attn", info.FlashAttention ? "on" : "off",
            "--parallel", Math.Max(1, info.ParallelSlots).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--no-webui",
            "--offline",
            "--fit", "off", // explicit, predictable placement; the app decides layers/context
        };
        if (info.ParallelSlots > 1) args.Add("--kv-unified");
        if (info.Embedding) args.Add("--embedding");
        else if (info.DisableReasoning) args.AddRange(["--reasoning", "off"]);
        return args;
    }

    private async Task WaitForHealthyAsync(HttpClient http, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw new InvalidOperationException($"llama-server exited with code {_process.ExitCode} during startup.\n{RecentLog}");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "health"));
                using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new TimeoutException($"llama-server did not become ready within {timeout.TotalSeconds:F0}s.\n{RecentLog}");
    }

    private void OnLogLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_logGate)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > MaxLogLines) _recentLog.Dequeue();
        }
        // llama-server's default verbosity logs timings and lifecycle, not prompt text.
        if (line.Contains(" E ", StringComparison.Ordinal)) _logger.LogWarning("[llama-server] {Line}", line);
        else _logger.LogDebug("[llama-server] {Line}", line);
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Error stopping llama-server");
        }
        _process.Dispose();
    }
}
