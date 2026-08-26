using System.Runtime.InteropServices;
using Whallee.Core.Interop;
using static Whallee.Core.Interop.NativeConstants;

namespace Whallee.Core.Desktop;

/// <summary>
/// A message-only window listening for the two events that invalidate our desktop attachment:
/// Explorer restarting, and the monitor layout changing.
/// </summary>
public sealed class ShellWatcher : IDisposable
{
    private const string ClassName = "WhalleeShellWatcher";
    private static readonly TimeSpan CrashLoopGuard = TimeSpan.FromSeconds(30);

    private readonly Action _onShellRestarted;
    private readonly Action _onDisplaysChanged;
    private readonly WndProc _wndProc;         // held so the GC cannot collect the thunk
    private readonly uint _taskbarCreatedMessage;

    private nint _handle;
    private uint _explorerPid;
    private DateTime _lastRestart = DateTime.MinValue;
    private bool _disposed;

    public ShellWatcher(Action onShellRestarted, Action onDisplaysChanged)
    {
        _onShellRestarted = onShellRestarted;
        _onDisplaysChanged = onDisplaysChanged;
        _wndProc = HandleMessage;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _explorerPid = CurrentExplorerPid();

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = Marshal.GetHINSTANCE(typeof(ShellWatcher).Module),
            lpszClassName = ClassName
        };
        var atom = NativeMethods.RegisterClassExW(ref wndClass);
        if (atom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS is fine on a second instance
                throw new InvalidOperationException($"RegisterClassExW failed for {ClassName}: {error}");
        }

        // Parent 0, NOT HWND_MESSAGE, and no WS_VISIBLE: an ordinary top-level window that is
        // simply never shown. See the amendment note below - this is load-bearing.
        _handle = NativeMethods.CreateWindowExW(
            0, ClassName, null, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (_handle == 0)
            throw new InvalidOperationException(
                $"CreateWindowExW failed for {ClassName}: {Marshal.GetLastWin32Error()}");
    }

    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == _taskbarCreatedMessage)
        {
            var pid = CurrentExplorerPid();
            if (pid != _explorerPid)
            {
                _explorerPid = pid;
                if (DateTime.UtcNow - _lastRestart > CrashLoopGuard)
                {
                    _lastRestart = DateTime.UtcNow;
                    _onShellRestarted();
                }
                // Inside the guard window: Explorer is crash-looping. Do nothing rather than
                // thrash re-attaching.
            }
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            _onDisplaysChanged();
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static uint CurrentExplorerPid()
    {
        var tray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return 0;
        NativeMethods.GetWindowThreadProcessId(tray, out var pid);
        return pid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != 0)
        {
            NativeMethods.DestroyWindow(_handle);
            _handle = 0;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);
}
