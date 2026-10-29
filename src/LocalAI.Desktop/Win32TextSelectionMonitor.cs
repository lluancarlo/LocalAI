using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using LocalAI.Core.Desktop;
using Microsoft.Extensions.Logging;

namespace LocalAI.Desktop;

/// <summary>
/// Reads the text the user selects with the mouse in other applications. A low-level mouse hook (on its own thread with
/// a message loop) notices selection gestures: a drag, a double- or triple-click, or a Shift+click. Shortly after the
/// button is released, Windows UI Automation asks the focused control (else the one under the mouse) for its selected
/// text, silently. Applications that do not share their selection that way (Notepad++, Unity, VS Code, ...) get a
/// Ctrl+C instead, and the user's clipboard is put back (<see cref="ClipboardSelectionCopier"/>); not when the mouse was
/// on a scroll bar, title bar, tab, button or similar control, where the drag was not a text selection. Password
/// fields and this application are ignored.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32TextSelectionMonitor(ILogger<Win32TextSelectionMonitor> logger) : ITextSelectionMonitor
{
    public bool IsSupported => true;

    public IDisposable Start(Action<string> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return new Watch(selected, logger);
    }

    private sealed partial class Watch : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const uint WM_QUIT = 0x0012, WM_USER = 0x0400, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
        private const uint PM_NOREMOVE = 0x0000;
        private static readonly IntPtr HWND_MESSAGE = -3;
        /// <summary>
        /// UI Automation control types under the mouse where a drag or click is not a text selection, so no Ctrl+C is
        /// sent: button, check box, combo box, list item, menu, menu bar, menu item, scroll bar, slider, tab, tab item,
        /// toolbar, tree item, split button, thumb, header, header item, title bar.
        /// </summary>
        private static readonly HashSet<int> NotTextControlTypes =
            [50000, 50002, 50003, 50007, 50009, 50010, 50011, 50014, 50015, 50018, 50019, 50021, 50024, 50031, 50027, 50034, 50035, 50037];
        private const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37, SM_CXDRAG = 68, SM_CYDRAG = 69;
        private const int VK_SHIFT = 0x10;
        /// <summary>Time for the application to update its selection after the button is released.</summary>
        private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

        [ThreadStatic] private static Watch? t_current; // the watch whose hook runs on this thread

        private readonly Action<string> _selected;
        private readonly ILogger _logger;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        // Only the last gesture matters: an older one waiting to be read is replaced.
        private readonly Channel<POINT> _gestures = Channel.CreateBounded<POINT>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly Task _reader;
        private UiAutomationReader? _automation;
        private ClipboardSelectionCopier? _clipboard;
        private uint _threadId;
        private volatile bool _disposed;

        // Hook thread state.
        private POINT _downPoint;
        private uint _lastDownTime;
        private int _clicks;
        private bool _shiftDown;

        public Watch(Action<string> selected, ILogger logger)
        {
            _selected = selected;
            _logger = logger;
            _thread = new Thread(HookLoop) { IsBackground = true, Name = "Text selection hook" };
            _thread.Start();
            _ready.Wait();
            _reader = Task.Run(ReadLoopAsync);
        }

        private unsafe void HookLoop()
        {
            _threadId = GetCurrentThreadId();
            PeekMessageW(out _, IntPtr.Zero, WM_USER, WM_USER, PM_NOREMOVE); // creates this thread's message queue
            t_current = this;
            var hook = SetWindowsHookExW(WH_MOUSE_LL, (nint)(delegate* unmanaged<int, nint, nint, nint>)&HookProc, GetModuleHandleW(null), 0);
            if (hook == IntPtr.Zero) _logger.LogWarning("Could not watch the mouse (error {Error})", Marshal.GetLastPInvokeError());
            else _logger.LogDebug("Mouse hook installed");
            // Owns the clipboard while it is restored; this loop answers the clipboard messages Windows sends to it.
            var owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (owner != IntPtr.Zero) _clipboard = new ClipboardSelectionCopier(owner);
            _ready.Set();
            if (hook == IntPtr.Zero) return;
            try
            {
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            finally
            {
                UnhookWindowsHookEx(hook);
                if (owner != IntPtr.Zero) DestroyWindow(owner);
                t_current = null;
            }
        }

        [UnmanagedCallersOnly]
        private static unsafe nint HookProc(int code, nint message, nint data)
        {
            if (code >= 0 && t_current is { } watch)
            {
                try { watch.OnMouse((uint)message, (MSLLHOOKSTRUCT*)data); }
                catch { } // an exception must never cross into Windows
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        // Runs on the hook thread: must return quickly or Windows drops the hook.
        private unsafe void OnMouse(uint message, MSLLHOOKSTRUCT* info)
        {
            var pt = info->pt;
            switch (message)
            {
                case WM_LBUTTONDOWN:
                    var again = info->time - _lastDownTime <= GetDoubleClickTime()
                                && Math.Abs(pt.X - _downPoint.X) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2
                                && Math.Abs(pt.Y - _downPoint.Y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2;
                    _clicks = again ? _clicks + 1 : 1;
                    _downPoint = pt;
                    _lastDownTime = info->time;
                    _shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                    break;
                case WM_LBUTTONUP:
                    var dragged = Math.Abs(pt.X - _downPoint.X) > GetSystemMetrics(SM_CXDRAG)
                                  || Math.Abs(pt.Y - _downPoint.Y) > GetSystemMetrics(SM_CYDRAG);
                    var gesture = dragged || _clicks >= 2 || _shiftDown;
                    if (gesture) _gestures.Writer.TryWrite(pt);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        var (clicks, shift) = (_clicks, _shiftDown);
                        ThreadPool.QueueUserWorkItem(_ => _logger.LogDebug(
                            "Mouse released: dragged {Dragged}, clicks {Clicks}, shift {Shift}: {Action}",
                            dragged, clicks, shift, gesture ? "reading the selection" : "not a selection gesture"));
                    }
                    break;
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                await foreach (var point in _gestures.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await Task.Delay(SettleDelay).ConfigureAwait(false);
                    if (_disposed) break;
                    if (_gestures.Reader.TryPeek(out _)) continue; // a newer gesture replaces this one
                    string? text;
                    try
                    {
                        // Thread-pool threads are in the COM multithreaded apartment, which UI Automation clients need.
                        _automation ??= new UiAutomationReader();
                        string detail;
                        if (IsThisApplicationInFront()) (text, detail) = (null, "this application is in front");
                        else
                        {
                            text = _automation.ReadSelection(point, out detail, out var typeAtPoint);
                            if (text == null && _clipboard != null)
                            {
                                if (typeAtPoint is { } type && NotTextControlTypes.Contains(type)) detail += "; not a text control: not copying";
                                else
                                {
                                    text = _clipboard.Copy(out var copied);
                                    detail += "; " + copied;
                                }
                            }
                        }
                        _logger.LogDebug("Selection read: {Detail}", detail); // never the text itself
                    }
                    catch (Exception ex)
                    {
                        // Never log the text itself.
                        _logger.LogDebug("Could not read the selection: {Error}", ex.Message);
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(text) && !_disposed) _selected(text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Text selection reader failed");
            }
        }

        private static bool IsThisApplicationInFront()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
            return processId == (uint)Environment.ProcessId;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _gestures.Writer.TryComplete();
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            if (!_thread.Join(TimeSpan.FromSeconds(2))) _logger.LogWarning("The text selection hook thread did not stop");
            _ready.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public nuint dwExtraInfo;
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
        private static partial IntPtr SetWindowsHookExW(int idHook, nint lpfn, IntPtr hmod, uint dwThreadId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWindowsHookEx(IntPtr hhk);

        [LibraryImport("user32.dll")]
        private static partial nint CallNextHookEx(IntPtr hhk, int nCode, nint wParam, nint lParam);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr GetModuleHandleW(string? lpModuleName);

        [LibraryImport("user32.dll")]
        private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TranslateMessage(ref MSG msg);

        [LibraryImport("user32.dll")]
        private static partial nint DispatchMessageW(ref MSG msg);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostThreadMessageW(uint idThread, uint msg, nuint wParam, nint lParam);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentThreadId();

        [LibraryImport("user32.dll")]
        private static partial uint GetDoubleClickTime();

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

/// <summary>Reads the selected text of a control through the UI Automation client API (UIAutomationCore, COM).</summary>
[SupportedOSPlatform("windows")]
internal sealed class UiAutomationReader
{
    private const int UIA_TextPatternId = 10014;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_ControlTypePropertyId = 30003;
    /// <summary>Longest text read from one selection (the caller cuts it further).</summary>
    private const int MaxChars = 100_000;
    private static readonly Guid CUIAutomationClsid = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private readonly IUIAutomation _automation =
        (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(CUIAutomationClsid, throwOnError: true)!)!;

    /// <summary>The selection of the focused control, else of the control at <paramref name="point"/>; null if none.</summary>
    /// <param name="detail">What was found, for diagnostics (control types and lengths, never the text).</param>
    /// <param name="typeAtPoint">The control type at <paramref name="point"/>, when it was looked at.</param>
    public string? ReadSelection(POINT point, out string detail, out int? typeAtPoint)
    {
        typeAtPoint = null;
        var text = ReadSelection(_automation.GetFocusedElement(), out var focused);
        if (text != null)
        {
            detail = $"focused {focused}";
            return text;
        }
        var element = _automation.ElementFromPoint(point);
        typeAtPoint = element?.GetCurrentPropertyValue(UIA_ControlTypePropertyId) as int?;
        text = ReadSelection(element, out var atPoint);
        detail = $"focused {focused}; at the mouse {atPoint}";
        return text;
    }

    /// <summary>The selected text of <paramref name="element"/>, or null if it has none or does not expose it.</summary>
    internal static string? ReadSelection(IUIAutomationElement? element) => ReadSelection(element, out _);

    private static string? ReadSelection(IUIAutomationElement? element, out string detail)
    {
        if (element == null)
        {
            detail = "no control";
            return null;
        }
        var type = $"control type {element.GetCurrentPropertyValue(UIA_ControlTypePropertyId)}";
        if (element.GetCurrentPropertyValue(UIA_IsPasswordPropertyId) is true)
        {
            detail = type + ", password field";
            return null;
        }
        if (element.GetCurrentPattern(UIA_TextPatternId) is not IUIAutomationTextPattern pattern)
        {
            detail = type + ", no text pattern";
            return null;
        }
        var ranges = pattern.GetSelection();
        if (ranges == null)
        {
            detail = type + ", no selection";
            return null;
        }
        var text = new StringBuilder();
        var count = ranges.get_Length();
        for (var i = 0; i < count && text.Length < MaxChars; i++)
        {
            var part = ranges.GetElement(i)?.GetText(MaxChars - text.Length);
            if (string.IsNullOrWhiteSpace(part)) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(part);
        }
        var result = text.ToString().Trim();
        detail = $"{type}, {count} range(s), {result.Length} characters";
        return result.Length == 0 ? null : result;
    }

    internal IUIAutomation Automation => _automation;
}

// Minimal UI Automation client interfaces (UIAutomationClient.h). Methods are declared in vtable order; the ones this
// app never calls are placeholders that only keep the slots aligned.

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    void ElementFromHandle();
    IUIAutomationElement? ElementFromPoint(POINT pt);
    IUIAutomationElement? GetFocusedElement();
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId();
    void FindFirst();
    void FindAll();
    void FindFirstBuildCache();
    void FindAllBuildCache();
    void BuildUpdatedCache();
    [return: MarshalAs(UnmanagedType.Struct)]
    object? GetCurrentPropertyValue(int propertyId);
    void GetCurrentPropertyValueEx();
    void GetCachedPropertyValue();
    void GetCachedPropertyValueEx();
    void GetCurrentPatternAs();
    void GetCachedPatternAs();
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object? GetCurrentPattern(int patternId);
}

[ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextPattern
{
    void RangeFromPoint();
    void RangeFromChild();
    IUIAutomationTextRangeArray? GetSelection();
    void GetVisibleRanges();
    IUIAutomationTextRange? get_DocumentRange();
}

[ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRangeArray
{
    int get_Length();
    IUIAutomationTextRange? GetElement(int index);
}

[ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTextRange
{
    void Clone();
    void Compare();
    void CompareEndpoints();
    void ExpandToEnclosingUnit();
    void FindAttribute();
    void FindText();
    void GetAttributeValue();
    void GetBoundingRectangles();
    void GetEnclosingElement();
    [return: MarshalAs(UnmanagedType.BStr)]
    string? GetText(int maxLength);
}
