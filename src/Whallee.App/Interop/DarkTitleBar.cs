using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Whallee.App.Interop;

/// <summary>
/// Paints a window's system titlebar dark, so a dark-themed window does not wear a light-grey
/// caption bar.
/// </summary>
/// <remarks>
/// STANDARD CHROME IS KEPT DELIBERATELY. Only the caption colour changes: drag, snap, Alt+F4, the
/// system menu and per-monitor DPI all keep working, every one of which hand-rolled chrome would
/// have to reimplement. That matters more here than in most apps - this process owns windows
/// re-parented into Explorer's desktop hierarchy, so window handling is not a place to experiment.
/// </remarks>
public static class DarkTitleBar
{
    /// <summary>
    /// DWMWA_USE_IMMERSIVE_DARK_MODE on Windows 10 build 18985 and later.
    /// </summary>
    /// <remarks>
    /// The older value 19 is deliberately NOT attempted as a fallback. This app targets Windows 11,
    /// so that branch could never run on a supported machine - it would be untestable code whose
    /// only effect is to look thorough.
    /// </remarks>
    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// Applies the dark caption bar - now if the window already has a handle, and on
    /// <see cref="Window.SourceInitialized"/> if it does not.
    /// </summary>
    /// <remarks>
    /// Safe to call straight after InitializeComponent, which is the normal case and where the
    /// window has no HWND yet.
    /// </remarks>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += OnSourceInitialized;
            return;
        }

        Set(handle);
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;

        // Unsubscribed immediately: SourceInitialized can fire again if a window's handle is
        // recreated, and re-applying is pointless work on a callback that is not ours to own.
        window.SourceInitialized -= OnSourceInitialized;
        Set(new WindowInteropHelper(window).Handle);
    }

    /// <remarks>
    /// EVERY FAILURE IS SWALLOWED ON PURPOSE, and the HRESULT is ignored. This is cosmetic: a
    /// missing dwmapi.dll or a rejected attribute must leave a light titlebar, never take down a
    /// dialog the user was trying to open.
    /// </remarks>
    private static void Set(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        try
        {
            var enabled = 1;
            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
