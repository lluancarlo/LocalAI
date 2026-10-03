using System.Diagnostics;
using System.Globalization;
using LocalAI.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LocalAI.Infrastructure;

/// <summary>Reads GPU information from the local <c>nvidia-smi</c> tool (part of the NVIDIA driver).</summary>
public sealed class NvidiaSmiGpuInfoProvider(ILogger<NvidiaSmiGpuInfoProvider> logger) : IGpuInfoProvider
{
    private string? _cudaVersion;

    public async Task<GpuInfo?> GetGpuInfoAsync(CancellationToken ct = default)
    {
        var csv = await RunAsync("--query-gpu=name,memory.total,memory.used,driver_version,compute_cap --format=csv,noheader,nounits", ct)
            .ConfigureAwait(false);
        var line = csv?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line == null) return null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 4) return null;
        _cudaVersion ??= await GetCudaVersionAsync(ct).ConfigureAwait(false);
        return new GpuInfo(
            parts[0],
            ParseInt(parts[1]),
            ParseInt(parts[2]),
            parts[3],
            _cudaVersion,
            parts.Length > 4 ? parts[4] : null);
    }

    public async Task<int?> GetProcessMemoryMbAsync(int processId, CancellationToken ct = default)
    {
        var csv = await RunAsync("--query-compute-apps=pid,used_memory --format=csv,noheader,nounits", ct).ConfigureAwait(false);
        if (csv == null) return null;
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && pid == processId &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
                return mb;
        }
        return null; // WDDM often reports N/A per process; caller falls back to a before/after delta
    }

    private async Task<string?> GetCudaVersionAsync(CancellationToken ct)
    {
        var text = await RunAsync("", ct).ConfigureAwait(false);
        if (text == null) return null;
        foreach (var marker in new[] { "CUDA Version:", "CUDA UMD Version:" })
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = text[(i + marker.Length)..].TrimStart();
            var end = rest.IndexOfAny([' ', '|', '\n']);
            return end > 0 ? rest[..end] : rest;
        }
        return null;
    }

    private async Task<string?> RunAsync(string args, CancellationToken ct)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return null;
            var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogInformation("nvidia-smi not available: {Message}", ex.Message);
            return null;
        }
    }

    private static int ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
