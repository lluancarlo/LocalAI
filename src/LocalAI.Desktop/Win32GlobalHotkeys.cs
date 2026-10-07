using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAI.Core.Desktop;
using Microsoft.Extensions.Logging;

namespace LocalAI.Desktop;

/// <summary>
/// Global shortcuts through Win32 <c>RegisterHotKey</c>. Shortcuts belong to a dedicated thread with its own message
/// loop (no window needed), so they keep working while the app's windows are hidden. Registration calls are marshalled
/// to that thread; pressed callbacks run on the thread pool so a slow handler never blocks the loop.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32GlobalHotkeys : IGlobalHotkeys, IDisposable
{
    private const uint WM_QUIT = 0x0012;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_USER = 0x0400;
    private const uint WM_RUN_WORK = 0x8000 + 1; // WM_APP + 1
    private const uint PM_NOREMOVE = 0x0000;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const int MaxHotkeyId = 0xBFFF; // ids above are reserved for shared DLLs
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<Win32GlobalHotkeys> _logger;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action<bool>> _work = new(); // argument: false = the loop has ended, fail instead of running
    private readonly ManualResetEventSlim _ready = new();
    // Touched only on the hotkey thread.
    private readonly Dictionary<int, Action> _handlers = [];
    private int _lastId;
    private uint _threadId;
    private volatile bool _disposed;

    public Win32GlobalHotkeys(ILogger<Win32GlobalHotkeys> logger)
    {
        _logger = logger;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "Global hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    public IDisposable Register(HotkeyGesture gesture, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        ArgumentNullException.ThrowIfNull(pressed);
        var modifiers = ToNative(gesture.Modifiers) | MOD_NOREPEAT;
        var key = VirtualKey(gesture.Key);
        var id = Invoke(() =>
        {
            var id = NextId();
            if (!RegisterHotKey(IntPtr.Zero, id, modifiers, key))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new HotkeyUnavailableException(gesture, error == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? $"{gesture} is already used by another application or by Windows."
                    : $"{gesture} could not be registered: {new Win32Exception(error).Message}");
            }
            _handlers[id] = pressed;
            return id;
        });
        _logger.LogDebug("Registered global shortcut {Hotkey} (id {Id})", gesture, id);
        return new Registration(this, id);
    }

    private void Unregister(int id)
    {
        if (_disposed) return; // the loop unregisters everything when it ends
        try
        {
            Invoke(() =>
            {
                if (_handlers.Remove(id)) UnregisterHotKey(IntPtr.Zero, id);
                return 0;
            });
        }
        catch (ObjectDisposedException) { }
    }

    private int NextId()
    {
        for (var attempt = 0; attempt < MaxHotkeyId; attempt++)
        {
            _lastId = _lastId % MaxHotkeyId + 1;
            if (!_handlers.ContainsKey(_lastId)) return _lastId;
        }
        throw new InvalidOperationException("Too many global shortcuts.");
    }

    /// <summary>Runs <paramref name="action"/> on the hotkey thread (RegisterHotKey binds a shortcut to the calling thread).</summary>
    private T Invoke<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) return action();

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(run =>
        {
            if (!run)
            {
                done.TrySetException(new ObjectDisposedException(nameof(Win32GlobalHotkeys)));
                return;
            }
            try { done.SetResult(action()); }
            catch (Exception ex) { done.SetException(ex); }
        });
        if (!PostThreadMessageW(_threadId, WM_RUN_WORK, 0, 0))
            throw new ObjectDisposedException(nameof(Win32GlobalHotkeys), "The global shortcut thread has stopped.");
        // Rethrows the action's own exception (not an AggregateException); TimeoutException if the thread is stuck.
        return done.Task.WaitAsync(InvokeTimeout).GetAwaiter().GetResult();
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, IntPtr.Zero, WM_USER, WM_USER, PM_NOREMOVE); // creates this thread's message queue
        _ready.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            switch (msg.message)
            {
                case WM_HOTKEY when _handlers.TryGetValue((int)msg.wParam, out var handler):
                    ThreadPool.QueueUserWorkItem(_ => RunHandler(handler));
                    break;
                case WM_RUN_WORK:
                    while (_work.TryDequeue(out var work)) work(true);
                    break;
            }
        }

        foreach (var id in _handlers.Keys) UnregisterHotKey(IntPtr.Zero, id);
        _handlers.Clear();
        while (_work.TryDequeue(out var pending)) pending(false);
    }

    private void RunHandler(Action handler)
    {
        try { handler(); }
        catch (Exception ex) { _logger.LogError(ex, "Global shortcut handler failed"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(2))) _logger.LogWarning("The global shortcut thread did not stop");
        _ready.Dispose();
    }

    private static uint ToNative(HotkeyModifiers modifiers) =>
        (modifiers.HasFlag(HotkeyModifiers.Ctrl) ? MOD_CONTROL : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Alt) ? MOD_ALT : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Shift) ? MOD_SHIFT : 0) |
        (modifiers.HasFlag(HotkeyModifiers.Win) ? MOD_WIN : 0);

    /// <summary>Windows virtual-key code of a <see cref="HotkeyGesture.Key"/> name.</summary>
    internal static uint VirtualKey(string key) => key switch
    {
        { Length: 1 } when key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9' => key[0],
        _ when key.StartsWith("NumPad", StringComparison.Ordinal) => 0x60u + uint.Parse(key.AsSpan(6), provider: null),
        _ when key.Length > 1 && key[0] == 'F' && char.IsAsciiDigit(key[1]) => 0x6Fu + uint.Parse(key.AsSpan(1), provider: null),
        "Space" => 0x20,
        "PageUp" => 0x21,
        "PageDown" => 0x22,
        "End" => 0x23,
        "Home" => 0x24,
        "Left" => 0x25,
        "Up" => 0x26,
        "Right" => 0x27,
        "Down" => 0x28,
        "Insert" => 0x2D,
        "Delete" => 0x2E,
        "Pause" => 0x13,
        _ => throw new ArgumentException($"Unsupported shortcut key: {key}", nameof(key)),
    };

    private sealed class Registration(Win32GlobalHotkeys owner, int id) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Unregister(id);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
        public uint lPrivate;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
