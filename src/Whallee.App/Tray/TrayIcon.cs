using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon.Core;

namespace Whallee.App.Tray;

/// <summary>Tray presence for the app. The menu grows in Task 11 (Next clip, Pause/Resume).</summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// MAKEINTRESOURCE(32512). Two meanings, and both are used below: passed with a NULL module it
    /// is IDI_APPLICATION, the stock Windows icon; passed with OUR module it is the resource id the
    /// .NET SDK embeds <c>&lt;ApplicationIcon&gt;</c> under, so it resolves to Whallee's own icon.
    /// Verified 2026-09-03 against the built exe: id 32512 resolves, id 1 does not.
    /// </summary>
    private const int IdiApplication = 32512;

    private const uint ImageIcon = 1;

    /// <summary>Standard small-icon metrics — SM_CXSMICON / SM_CYSMICON.</summary>
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;

    /// <summary>
    /// Returns a SHARED handle, freed by the system. Required here: the disposal contract below
    /// frees nothing, and using LoadImage without it would leak an icon per app run.
    /// </summary>
    private const uint LrShared = 0x00008000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadImageW(
        nint hInst, nint name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetModuleHandleW(nint lpModuleName);

    /// <summary>
    /// Whallee's own icon at the size the tray actually draws, falling back to the stock icon.
    /// </summary>
    /// <remarks>
    /// LoadImage AT SM_CXSMICON, NOT LoadIcon. LoadIcon only ever returns the large (32 px) entry,
    /// which the shell would then downscale; the .ico embedded in this exe carries a real 16 px
    /// entry drawn for that size, and asking for the small metrics is what selects it. On a
    /// high-DPI display the metrics come back larger and the matching entry is chosen instead.
    ///
    /// EVERY HANDLE THIS CAN RETURN IS SHARED AND MUST NEVER BE DESTROYED — LoadImage because of
    /// LR_SHARED, LoadIcon because stock icons always are. That is why Dispose frees nothing.
    ///
    /// The fallback is not decoration: Shell_NotifyIcon with a null icon leaves the shell nothing
    /// to draw, so the tray entry becomes an invisible gap. A wrong-looking icon is recoverable;
    /// a missing one looks like the app failed to start.
    /// </remarks>
    private static nint LoadAppIcon()
    {
        var module = GetModuleHandleW(0);

        if (module != 0)
        {
            var icon = LoadImageW(
                module, IdiApplication, ImageIcon,
                GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), LrShared);

            if (icon != 0) return icon;
        }

        return LoadIconW(0, IdiApplication);
    }

    private readonly TrayIconWithContextMenu _icon;
    private bool _disposed;

    /// <param name="onShow">Bring the window back. Marshalled to the UI thread — see remarks.</param>
    /// <param name="onExit">Quit the app. Marshalled to the UI thread — see remarks.</param>
    /// <remarks>
    /// EVERY CALLBACK IS MARSHALLED, and that is not defensive tidiness. H.NotifyIcon raises menu
    /// clicks and mouse events on its own message-window thread, NOT the WPF Dispatcher thread.
    /// Touching a Window or calling Application.Shutdown from there hits Dispatcher.VerifyAccess,
    /// which throws InvalidOperationException — unhandled, on a non-UI thread, so it kills the
    /// process. Measured: choosing tray Exit crashed the app instead of quitting it, and Windows
    /// Error Reporting took roughly ten seconds to tear it down. The mpv children died with it via
    /// the job object, so the failure looked like a slow but successful exit.
    ///
    /// InvokeAsync rather than Invoke: the tray's thread must not block waiting on the UI thread,
    /// which during a restart can be busy inside the engine for seconds at a time.
    /// </remarks>
    public TrayIcon(Action onShow, Action onExit)
    {
        // Captured here because the constructor runs on the UI thread.
        var dispatcher = Application.Current.Dispatcher;
        void OnUiThread(Action action) => dispatcher.InvokeAsync(action);

        var menu = new PopupMenu();
        menu.Items.Add(new PopupMenuItem("Show Whallee", (_, _) => OnUiThread(onShow)));
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem("Exit", (_, _) => OnUiThread(onExit)));

        _icon = new TrayIconWithContextMenu
        {
            ToolTip = "Whallee",
            ContextMenu = menu,

            // Whallee's own icon, the same image embedded in the exe and shown on the desktop and
            // logon shortcuts - so the app looks like one thing everywhere. Was the stock Windows
            // icon until 2026-09-03; see LoadAppIcon for how the resource is found and why the
            // handle is never freed.
            Icon = LoadAppIcon()
        };

        _icon.MessageWindow.MouseEventReceived += (_, args) =>
        {
            if (args.MouseEvent == MouseEvent.IconDoubleClick) OnUiThread(onShow);
        };

        _icon.Create();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Dispose();
    }
}
