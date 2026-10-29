using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LocalAI.Desktop;

/// <summary>
/// Fallback for applications that do not share their selection through UI Automation (Notepad++, Unity, VS Code, ...):
/// sends Ctrl+C to the window in front, reads the copied text and puts the user's clipboard back. To stay harmless it
/// does nothing when the window in front is a terminal (Ctrl+C would stop the running program), when a key or mouse
/// button is held (the keys would combine into another shortcut), or when the clipboard holds something other than
/// text (files, an image), which could not be restored. The restored text is hidden from clipboard history.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ClipboardSelectionCopier(IntPtr owner)
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11, VK_C = 0x43;
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Keys and buttons that must be released: Shift, Ctrl, Alt, both Win keys, left and right mouse buttons.</summary>
    private static readonly int[] HeldKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C, 0x01, 0x02];

    /// <summary>Window classes of terminals, where Ctrl+C stops the running program.</summary>
    private static readonly HashSet<string> TerminalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "VirtualConsoleClass", "mintty", "PuTTY",
    };

    private static readonly uint ExcludeFromHistoryFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");

    /// <summary>The selected text copied from the window in front, or null.</summary>
    /// <param name="detail">Why nothing was copied, or the length copied (never the text).</param>
    public string? Copy(out string detail)
    {
        var window = GetForegroundWindow();
        if (TerminalClasses.Contains(ClassName(window)))
        {
            detail = "terminal in front: not copying";
            return null;
        }
        if (HeldKeys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
        {
            detail = "a key or button is held: not copying";
            return null;
        }

        string? saved;
        if (!TryOpen())
        {
            detail = "clipboard busy";
            return null;
        }
        try
        {
            saved = ReadText();
            if (saved == null && CountClipboardFormats() > 0)
            {
                detail = "clipboard holds files or an image: not copying";
                return null;
            }
        }
        finally
        {
            CloseClipboard();
        }

        var before = GetClipboardSequenceNumber();
        if (!SendCtrlC())
        {
            detail = "could not send Ctrl+C";
            return null;
        }
        var wait = Stopwatch.StartNew();
        while (GetClipboardSequenceNumber() == before && wait.Elapsed < CopyTimeout) Thread.Sleep(15);
        if (GetClipboardSequenceNumber() == before)
        {
            detail = "nothing was copied";
            return null;
        }

        string? copied = null;
        try
        {
            Thread.Sleep(30); // let the application finish writing all its clipboard formats
            if (TryOpen())
            {
                try { copied = ReadText(); }
                finally { CloseClipboard(); }
            }
        }
        finally
        {
            Restore(saved);
        }
        copied = copied?.Trim();
        detail = $"copied {copied?.Length ?? 0} characters";
        return string.IsNullOrEmpty(copied) ? null : copied;
    }

    /// <summary>Puts the user's text back (or empties the clipboard if it was empty), hidden from clipboard history.</summary>
    private void Restore(string? text)
    {
        if (!TryOpen()) return;
        try
        {
            EmptyClipboard();
            if (text != null) SetData(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
            if (ExcludeFromHistoryFormat != 0) SetData(ExcludeFromHistoryFormat, [0]);
        }
        finally
        {
            CloseClipboard();
        }
    }

    private bool TryOpen()
    {
        for (var i = 0; i < 20; i++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(10); // another application has it open
        }
        return false;
    }

    private static string? ReadText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero) return null;
        var data = GlobalLock(handle);
        if (data == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(data, (int)(GlobalSize(handle) / 2)).Split('\0')[0]; }
        finally { GlobalUnlock(handle); }
    }

    private static void SetData(uint format, byte[] bytes)
    {
        var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (handle == IntPtr.Zero) return;
        var data = GlobalLock(handle);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) == IntPtr.Zero) GlobalFree(handle); // on success the clipboard owns it
    }

    private static bool SendCtrlC()
    {
        INPUT[] inputs =
        [
            Key(VK_CONTROL, 0), Key(VK_C, 0), Key(VK_C, KEYEVENTF_KEYUP), Key(VK_CONTROL, KEYEVENTF_KEYUP),
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private static INPUT Key(ushort vk, uint flags) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };

    private static string ClassName(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassNameW(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi; // the largest member: gives the union its size
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] INPUT[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(IntPtr hWnd, [Out] char[] className, int maxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr newOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial int CountClipboardFormats();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetClipboardData(uint format, IntPtr data);

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalFree(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(IntPtr handle);
}
