using System.Runtime.InteropServices;
using Whallee.Core.Displays;
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
    private readonly Action<string> _onError;
    private readonly WndProc _wndProc;         // held so the GC cannot collect the thunk
    private readonly uint _taskbarCreatedMessage;

    private nint _handle;
    private uint _explorerPid;
    private string _topology;
    private DateTime _lastRestart = DateTime.MinValue;
    private bool _disposed;

    /// <param name="onShellRestarted">Explorer has restarted. MUST NOT block — see HandleMessage.</param>
    /// <param name="onDisplaysChanged">The monitor layout changed. MUST NOT block.</param>
    /// <param name="onError">
    /// Reports a failure that would otherwise have crossed the native frame. MUST NOT block and
    /// MUST NOT throw: it is called from inside a WndProc, on the thread whose input queue is
    /// attached to Explorer's desktop, so anything unbounded here — console I/O included — freezes
    /// the user's desktop. Hand it something that only enqueues.
    /// </param>
    public ShellWatcher(Action onShellRestarted, Action onDisplaysChanged, Action<string> onError)
    {
        _onShellRestarted = onShellRestarted;
        _onDisplaysChanged = onDisplaysChanged;
        _onError = onError;
        _wndProc = HandleMessage;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        _explorerPid = CurrentExplorerPid();
        _topology = CurrentTopology();

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

    /// <remarks>
    /// This is a reverse-P/Invoke callback: user32 calls it from inside DispatchMessageW. A managed
    /// exception must therefore never leave this method. Letting one propagate would unwind across
    /// the native user32 frame, skipping user32's own cleanup, and then escape through
    /// MessagePump.RunUntil as an unhandled exception that kills the process while three mpv
    /// processes are mid-teardown. Plenty on the Restart() path can throw — MessagePump.Sleep on
    /// WAIT_FAILED, HostWindow's CreateWindowExW check, DesktopLayer and DisplayEnumerator, none of
    /// which sit inside Start()'s per-display try — so the whole body is guarded and always falls
    /// through to DefWindowProcW.
    /// </remarks>
    private nint HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
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
                // Guarded the same way the Explorer branch above guards on _explorerPid, and for
                // the same reason: the broadcast fires more often than the thing it announces
                // actually happens. Measured on this machine, one arrives ~0.7 s into EVERY
                // startup with the layout unchanged, which cost a full teardown and rebuild of
                // every player on every launch.
                //
                // ACCEPTED RISK, stated plainly: if a broadcast ever arrived BEFORE the new layout
                // was visible to EnumDisplayDevices, this would suppress a real change and the
                // wallpapers would sit on stale geometry until the next event. WM_DISPLAYCHANGE is
                // documented as sent after the change is applied, and its own wParam/lParam carry
                // the NEW mode, so that ordering would be a platform contract violation. Erring the
                // other way — restarting on every broadcast — was the measured bug, not a theory.
                var current = CurrentTopology();
                if (!string.Equals(current, _topology, StringComparison.Ordinal))
                {
                    _topology = current;
                    _onDisplaysChanged();
                }
            }
        }
        catch (Exception ex)
        {
            // Never rethrow. Report it and carry on: the engine may be half-attached, but a live
            // desktop with one broken monitor beats a dead process that left mpv running.
            try
            {
                _onError($"watcher failed handling message 0x{msg:X}: {ex.Message}");
            }
            catch
            {
                // The reporter itself failed. There is nothing left to report it to, and throwing
                // from here would land us back on the native frame this catch exists to protect.
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Snapshot of the monitor layout. Enumeration is a pair of user32 calls with no I/O, which is
    /// what makes it safe to do from inside the WndProc on the pump thread; anything slower here
    /// would freeze the user's desktop. A failure returns the previous signature so a transient
    /// enumeration error cannot masquerade as a layout change and trigger a spurious restart.
    /// </summary>
    private string CurrentTopology()
    {
        try
        {
            return DisplayTopology.Signature(DisplayEnumerator.Enumerate());
        }
        catch (Exception)
        {
            return _topology ?? "";
        }
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
