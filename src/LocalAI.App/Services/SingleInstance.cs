using System.Security.Cryptography;
using System.Text;

namespace LocalAI.App.Services;

/// <summary>
/// One running copy per app folder: the app lives in the tray, so a second launch would fight the first one for the
/// shortcuts, the microphone and the GPU. A second launch asks the running copy to show its window and exits.
/// Uses named kernel objects only (nothing is written to disk).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequested;
    private RegisteredWaitHandle? _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequested)
    {
        _mutex = mutex;
        _showRequested = showRequested;
    }

    /// <summary>Becomes the running copy for <paramref name="appFolder"/>, or returns null after asking the running copy to show itself.</summary>
    public static SingleInstance? TryAcquire(string appFolder)
    {
        var name = $@"Local\LocalAI-{FolderKey(appFolder)}";
        var mutex = new Mutex(initiallyOwned: true, name + "-instance", out var createdNew);
        var showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-show");
        if (createdNew) return new SingleInstance(mutex, showRequested);

        showRequested.Set();
        showRequested.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Runs <paramref name="show"/> (on a thread-pool thread) whenever another launch asks for the window.</summary>
    public void OnShowRequested(Action show) =>
        _wait = ThreadPool.RegisterWaitForSingleObject(_showRequested, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);

    private static string FolderKey(string appFolder)
    {
        var normalized = Path.GetFullPath(appFolder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)), 0, 8);
    }

    /// <summary>Must be called on the thread that acquired the instance (the main thread).</summary>
    public void Dispose()
    {
        _wait?.Unregister(null);
        _showRequested.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
