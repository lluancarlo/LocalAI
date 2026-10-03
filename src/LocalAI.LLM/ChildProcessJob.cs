using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LocalAI.LLM;

/// <summary>
/// Windows Job Object with KILL_ON_JOB_CLOSE: child engine processes die with the app, even if it crashes,
/// so no orphaned llama-server keeps holding VRAM. No-op on other platforms.
/// </summary>
internal static partial class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    public static void Assign(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = Job.Value;
        if (job == IntPtr.Zero) return;
        if (!AssignProcessToJobObject(job, process.Handle))
            Trace.TraceWarning($"AssignProcessToJobObject failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    private static IntPtr CreateJob()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return handle;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
