using System.Runtime.InteropServices;

namespace Whallee.Core.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width  => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAY_DEVICE
{
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public int StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WNDCLASSEX
{
    public uint cbSize;
    public uint style;
    public nint lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public string? lpszMenuName;
    public string lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public int  ptX;
    public int  ptY;
}

internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);
internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);
internal delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

internal static class NativeMethods
{
    private const string User32 = "user32.dll";

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint SendMessageTimeoutW(
        nint hWnd, uint msg, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

    [DllImport(User32, SetLastError = true)]
    public static extern nint SetParent(nint child, nint newParent);

    [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    // Mandatory after adding WS_EX_LAYERED: per MSDN a layered window is not displayed until
    // either this or UpdateLayeredWindow has been called. crKey is the colour key; alpha is 0-255.
    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte alpha, uint flags);

    [DllImport(User32, SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    // No SetLastError: GetSystemMetrics returns 0 both for failure and for a legitimately zero
    // metric, so the last error would not disambiguate anything. SM_X/YVIRTUALSCREEN are always
    // available on a system with a desktop.
    [DllImport(User32)]
    public static extern int GetSystemMetrics(int index);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX info);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevicesW(
        string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wndClass);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hWnd);

    // No SetLastError: a false return means "that is not a window", which is the answer rather
    // than a failure. Used to validate a CACHED WorkerW handle before reusing it — Explorer can
    // restart between passes, and a stale handle would otherwise be handed to SetParent.
    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint hWnd);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int cmdShow);

    // Returns >0 for a real message, 0 for WM_QUIT, and -1 on error - so callers must test
    // the sign, not truthiness. Blocks until a message arrives.
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern nint DispatchMessageW(ref MSG msg);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint threadId, uint msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // Blocks until either a handle is signaled or a new message arrives in the calling thread's
    // queue (dwWakeMask), returning WAIT_OBJECT_0 (+n) or WAIT_OBJECT_0 + handleCount for input.
    // MWMO_INPUTAVAILABLE ensures a message already queued before the call still wakes it.
    // handles is nullable so a zero-handle wait (MessagePump.Sleep) can pass null, which
    // marshals to NULL - the documented way to wait on no handles, just the message queue.
    [DllImport(User32, SetLastError = true)]
    public static extern uint MsgWaitForMultipleObjectsEx(
        uint handleCount, nint[]? handles, uint milliseconds, uint wakeMask, uint flags);

    // Non-blocking: removes one message from the queue if present, returns false if empty.
    // Correct here because the caller only calls it after being told the queue has input.
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax, uint flags);
}
