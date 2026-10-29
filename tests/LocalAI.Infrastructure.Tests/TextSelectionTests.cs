using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LocalAI.Desktop;

namespace LocalAI.Infrastructure.Tests;

/// <summary>
/// Reads the selection of a real RichEdit control (a hidden window of the test) through the UI Automation interop,
/// so a wrong slot in the hand-written COM interfaces fails here instead of in the app.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TextSelectionTests
{
    [Fact]
    [Trait("Category", "Desktop")] // needs an interactive Windows session (skipped in CI)
    public async Task Reads_the_selected_text_of_a_standard_text_control()
    {
        using var edit = new EditWindow("Hello selection probe", selectFrom: 6, selectTo: 15);
        var element = ((IUIAutomationFromHandle)new UiAutomationReader().Automation).ElementFromHandle(edit.Handle);

        var text = await Task.Run(() => UiAutomationReader.ReadSelection(element));

        Assert.Equal("selection", text);
    }

    [Fact]
    [Trait("Category", "Desktop")]
    public void The_mouse_hook_starts_and_stops_cleanly()
    {
        var monitor = new Win32TextSelectionMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<Win32TextSelectionMonitor>.Instance);
        for (var i = 0; i < 3; i++)
        {
            var watch = monitor.Start(_ => { });
            watch.Dispose();
            watch.Dispose(); // twice is harmless
        }
    }

    [Fact]
    [Trait("Category", "Desktop")]
    public async Task A_control_without_a_selection_reports_nothing()
    {
        using var edit = new EditWindow("Hello selection probe", selectFrom: 3, selectTo: 3);
        var element = ((IUIAutomationFromHandle)new UiAutomationReader().Automation).ElementFromHandle(edit.Handle);

        Assert.Null(await Task.Run(() => UiAutomationReader.ReadSelection(element)));
    }

    /// <summary>A hidden RichEdit control on its own thread with a message loop (UI Automation talks to it through messages).</summary>
    private sealed class EditWindow : IDisposable
    {
        private const uint WS_POPUP = 0x80000000, ES_MULTILINE = 0x0004, EM_SETSEL = 0x00B1, WM_QUIT = 0x0012;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private uint _threadId;

        public EditWindow(string text, int selectFrom, int selectTo)
        {
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                LoadLibraryW("msftedit.dll"); // RichEdit, which implements the UI Automation text pattern itself
                Handle = CreateWindowExW(0, "RICHEDIT50W", text, WS_POPUP | ES_MULTILINE, 0, 0, 300, 100, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                SendMessageW(Handle, EM_SETSEL, selectFrom, selectTo);
                _ready.Set();
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                DestroyWindow(Handle);
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait();
            Assert.NotEqual(IntPtr.Zero, Handle);
        }

        public IntPtr Handle { get; private set; }

        public void Dispose()
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
            _ready.Dispose();
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern nint SendMessageW(IntPtr hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessageW(uint thread, uint msg, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string fileName);
    }
}

/// <summary>IUIAutomation up to ElementFromHandle (the app itself only needs the focused element and the one at a point).</summary>
[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationFromHandle
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    IUIAutomationElement? ElementFromHandle(IntPtr hwnd);
}
