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

        Attach(handle, display, layer);
        return new HostWindow(handle);
    }

    private static void Attach(nint handle, DisplayInfo display, DesktopLayerInfo layer)
    {
        if (layer.Mode == DesktopLayerMode.RaisedDesktop)
        {
            var style = (long)NativeMethods.GetWindowLongPtr(handle, GWL_STYLE);
            NativeMethods.SetWindowLongPtr(handle, GWL_STYLE, (nint)((style & ~WS_POPUP) | WS_CHILD));

            var exStyle = (long)NativeMethods.GetWindowLongPtr(handle, GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(handle, GWL_EXSTYLE, (nint)(exStyle | WS_EX_LAYERED));

            NativeMethods.SetParent(handle, layer.Progman);

            // Sit immediately behind the icon layer.
            NativeMethods.SetWindowPos(
                handle, layer.ShellDefView,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else
        {
            NativeMethods.SetParent(handle, layer.WorkerW);
        }

        // After reparenting, coordinates are relative to the new parent, which spans the whole
        // virtual desktop. Re-apply the display's position in that space.
        var originX = display.X - VirtualScreenOrigin().x;
        var originY = display.Y - VirtualScreenOrigin().y;
        NativeMethods.SetWindowPos(
            handle, 0, originX, originY, display.Width, display.Height, SWP_NOACTIVATE);
    }

    /// <summary>Top-left of the virtual desktop; DISPLAY1 sits at a negative Y on this machine.</summary>
    private static (int x, int y) VirtualScreenOrigin()
    {
        var all = DisplayEnumerator.Enumerate();
        return (all.Min(d => d.X), all.Min(d => d.Y));
    }

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
