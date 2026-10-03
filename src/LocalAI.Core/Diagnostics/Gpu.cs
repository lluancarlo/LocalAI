namespace LocalAI.Core.Diagnostics;

public sealed record GpuInfo(
    string Name,
    int TotalMemoryMb,
    int UsedMemoryMb,
    string DriverVersion,
    string? CudaVersion,
    string? ComputeCapability);

public interface IGpuInfoProvider
{
    /// <summary>Returns null when no supported GPU is present.</summary>
    Task<GpuInfo?> GetGpuInfoAsync(CancellationToken ct = default);

    /// <summary>GPU memory used by a process in MB, when the driver reports it.</summary>
    Task<int?> GetProcessMemoryMbAsync(int processId, CancellationToken ct = default);
}
