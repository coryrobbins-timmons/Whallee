using System.Runtime.InteropServices;
using Whallee.Core.Displays;
using Whallee.Core.Interop;
using static Whallee.Core.Interop.NativeConstants;

namespace Whallee.Core.Desktop;

/// <summary>
/// A borderless window living inside the desktop layer. It paints nothing itself — it exists
/// only to give mpv an HWND to render into.
/// </summary>
public sealed class HostWindow : IDisposable
{
    private const string ClassName = "WhalleeHostWindow";
    private static ushort _registeredClass;
    // The delegate must outlive every window, or the GC collects the thunk and Windows
    // calls into freed memory the first time it dispatches a message.
    private static readonly WndProc WndProcDelegate = NativeMethods.DefWindowProcW;

    public nint Handle { get; private set; }
    private bool _disposed;

    private HostWindow(nint handle) => Handle = handle;

    public static HostWindow CreateFor(DisplayInfo display, DesktopLayerInfo layer)
    {
        EnsureClassRegistered();

        var handle = NativeMethods.CreateWindowExW(
            exStyle: WS_EX_TOOLWINDOW,          // keep it out of Alt+Tab and the taskbar
            className: ClassName,
            windowName: null,
            style: WS_POPUP | WS_VISIBLE,
            x: display.X, y: display.Y,
            width: display.Width, height: display.Height,
            parent: 0, menu: 0, instance: 0, param: 0);

        if (handle == 0)
            throw new InvalidOperationException(
                $"CreateWindowExW failed for {display.DeviceName}: {Marshal.GetLastWin32Error()}");

        try
        {
            Attach(handle, display, layer);
        }
        catch
        {
            // Attach can fail after the window exists. Nothing has wrapped this HWND yet, so no
            // Dispose will ever reach it — and by this point the Classic path has already flipped
            // WS_POPUP to WS_CHILD, so a SetParent failure (a stale WorkerW handle after Explorer
            // restarted mid-Restart, say) leaves a visible full-screen window with a NULL class
            // brush and no WS_EX_TRANSPARENT: it paints nothing and swallows every mouse click on
            // that monitor for the rest of the session. WallpaperEngine catches per display and
            // keeps running, so nothing else would ever clean it up. Destroy it before letting the
            // failure out.
            NativeMethods.DestroyWindow(handle);
            throw;
        }

        return new HostWindow(handle);
    }

    private static void Attach(nint handle, DisplayInfo display, DesktopLayerInfo layer)
    {
        if (layer.Mode == DesktopLayerMode.RaisedDesktop)
        {
            // UNVERIFIED ON REAL HARDWARE as of 2026-08-25, and STILL unexercised after the
            // 2026-08-27 re-review: this machine reports mode Classic, so this branch has never
            // executed here. It is the branch that runs first on a machine whose Progman carries
            // WS_EX_NOREDIRECTIONBITMAP, so the defects below were fixed blind, by owner decision.
            // Treat any change here as untested — and if this branch ever does run and shows a
            // black desktop, the layered-window decision below is the first thing to revisit.
            //
            // DesktopLayer.Find requires layer.WorkerW to be non-zero to reach this branch even
            // though the branch parents to Progman and never reads WorkerW. That is deliberate and
            // not dead weight: a non-zero WorkerW is the evidence that the desktop layer was
            // actually located rather than half-found, and dropping the requirement would let us
            // attach to a Progman whose wallpaper layer does not exist yet.

            // GetWindowLongPtr/SetWindowLongPtr are intentionally left unchecked: both
            // legitimately return 0 when the previous value was 0, so telling that apart
            // from failure requires clearing the last error first, which is out of scope here.
            var style = (long)NativeMethods.GetWindowLongPtr(handle, GWL_STYLE);
            NativeMethods.SetWindowLongPtr(handle, GWL_STYLE, (nint)((style & ~WS_POPUP) | WS_CHILD));

            // WS_EX_LAYERED, followed immediately by the SetLayeredWindowAttributes that MSDN makes
            // mandatory: a layered window is not displayed at all until either that or
            // UpdateLayeredWindow has been called. The two belong together — the style on its own
            // is an invisible window, which is exactly what this branch used to be.
            //
            // WHY LAYERED AT ALL, when the Classic path does not need it: this branch exists
            // precisely because Progman carries WS_EX_NOREDIRECTIONBITMAP, i.e. that top-level
            // window has NO redirection surface. An ordinary child window composites into its
            // top-level's redirection surface, so a plain child of this Progman plausibly has
            // nowhere to composite from and renders nothing — which is very likely why the style
            // was here in the first place. A layered child is composited independently, with a
            // surface of its own. Note that "Classic works without it" does NOT transfer as an
            // argument: Classic parents into WorkerW, an ordinary top-level window that does have a
            // redirection surface.
            //
            // Neither option can be exercised on this machine, so the tiebreak is to take the one
            // MSDN documents. Alpha 255 with a zero colour key means fully opaque with no colour
            // keying: what is wanted here is the independent surface, not transparency.
            var exStyle = (long)NativeMethods.GetWindowLongPtr(handle, GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(handle, GWL_EXSTYLE, (nint)(exStyle | WS_EX_LAYERED));

            if (!NativeMethods.SetLayeredWindowAttributes(handle, 0, 255, LWA_ALPHA))
                throw new InvalidOperationException(
                    $"SetLayeredWindowAttributes failed for {display.DeviceName}: " +
                    $"{Marshal.GetLastWin32Error()}");

            if (NativeMethods.SetParent(handle, layer.Progman) == 0)
                throw new InvalidOperationException(
                    $"SetParent into Progman failed for {display.DeviceName}: {Marshal.GetLastWin32Error()}");

            // Sit immediately behind the icon layer. Checked, unlike a plain reposition: this is
            // the call that keeps the video *behind* the desktop icons, so a silent failure here
            // does not degrade gracefully — it puts video on top of the user's icons.
            if (!NativeMethods.SetWindowPos(
                    handle, layer.ShellDefView,
                    0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE))
            {
                throw new InvalidOperationException(
                    $"SetWindowPos (z-order behind the icon layer) failed for " +
                    $"{display.DeviceName}: {Marshal.GetLastWin32Error()}");
            }
        }
        else
        {
            // SetParent does not change the WS_CHILD/WS_POPUP bit, and SetWindowPos reads
            // that bit to decide whether x/y are parent-relative or screen-relative. Without
            // this the parent-relative coordinates below are misread as screen coordinates.
            var style = (long)NativeMethods.GetWindowLongPtr(handle, GWL_STYLE);
            NativeMethods.SetWindowLongPtr(handle, GWL_STYLE, (nint)((style & ~WS_POPUP) | WS_CHILD));

            if (NativeMethods.SetParent(handle, layer.WorkerW) == 0)
                throw new InvalidOperationException(
                    $"SetParent into WorkerW failed for {display.DeviceName}: {Marshal.GetLastWin32Error()}");
        }

        // After reparenting, coordinates are relative to the new parent, which spans the whole
        // virtual desktop. Re-apply the display's position in that space.
        var origin = VirtualScreenOrigin();
        var originX = display.X - origin.x;
        var originY = display.Y - origin.y;
        // SWP_NOZORDER: the raised branch already set its z-order deliberately above, and in
        // Classic each display gets one non-overlapping window, so sibling ordering doesn't matter.
        if (!NativeMethods.SetWindowPos(
                handle, 0, originX, originY, display.Width, display.Height,
                SWP_NOACTIVATE | SWP_NOZORDER))
        {
            throw new InvalidOperationException(
                $"SetWindowPos failed for {display.DeviceName}: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <summary>Top-left of the virtual desktop; DISPLAY1 sits at a negative Y on this machine.</summary>
    /// <remarks>
    /// Two cheap authoritative system metrics, not a Min() over DisplayEnumerator.Enumerate().
    /// The enumeration version was wrong twice over: it ran two full passes on consecutive lines
    /// (EnumDisplayMonitors plus one EnumDisplayDevicesW per monitor, so 6 driver queries per host
    /// window and 18 per Start(), all on the pump thread), and it silently dropped any monitor
    /// whose GetMonitorInfoW failed. On this machine \\.\DISPLAY1 sits at y = -124, so one
    /// transient failure mid-hotplug moved the computed origin to (0, 0) and offset *every* host
    /// window by 124 px. SM_XVIRTUALSCREEN/SM_YVIRTUALSCREEN cannot lose a monitor that way.
    /// </remarks>
    private static (int x, int y) VirtualScreenOrigin() =>
        (NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN),
         NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN));

    private static void EnsureClassRegistered()
    {
        if (_registeredClass != 0)
            return;

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            hInstance = Marshal.GetHINSTANCE(typeof(HostWindow).Module),
            lpszClassName = ClassName
        };

        _registeredClass = NativeMethods.RegisterClassExW(ref wndClass);
        if (_registeredClass == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1410) // ERROR_CLASS_ALREADY_EXISTS is fine on a second run in-process
                throw new InvalidOperationException($"RegisterClassExW failed: {error}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Handle != 0)
        {
            NativeMethods.DestroyWindow(Handle);
            Handle = 0;
        }
    }
}
