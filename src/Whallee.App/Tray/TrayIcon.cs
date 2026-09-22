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

    /// <summary>Unsubscribes the Explorer-restart handler. Null only if subscribing failed.</summary>
    private readonly IDisposable? _taskbarCreated;

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

        // EXPLORER RESTARTS AND TAKES THE TRAY ICON WITH IT. Windows rebuilds the notification
        // area from nothing and broadcasts TaskbarCreated; every app is responsible for re-adding
        // its own icon, and an app that does not simply vanishes from the tray.
        //
        // That happened to the owner on 2026-09-10. The app itself was untouched - still running,
        // still painting three wallpapers, engine correctly re-attached on the same event - but the
        // ONLY way to quit it or reopen its window is this icon, so losing it left no way out
        // except ending the process. Subscribed BEFORE Create so a restart during start-up is
        // caught too.
        //
        // The library reports the broadcast but does not act on it; re-creating is ours to do.
        _taskbarCreated = _icon.MessageWindow.SubscribeToTaskbarCreated(
            (_, _) => RestoreAfterExplorerRestart());

        _icon.Create();
    }

    /// <summary>
    /// Re-adds the icon after Explorer has rebuilt the notification area.
    /// </summary>
    /// <remarks>
    /// NOT MARSHALLED TO THE UI THREAD, unlike every callback above, and the difference is
    /// deliberate. Those touch WPF objects; this touches the tray icon, whose HWND is owned by
    /// H.NotifyIcon's own message-window thread - which is exactly the thread this fires on.
    /// Shell_NotifyIcon has to be called from the thread that owns the window, so hopping to the
    /// Dispatcher would be the wrong move here, not the safe one.
    ///
    /// REMOVE BEFORE CREATE: the shell has forgotten the icon but this object has not, so its
    /// IsCreated is still true and Create alone can be refused as a duplicate. The Remove is best
    /// effort - it is expected to fail, because the thing it would remove is already gone.
    ///
    /// EVERYTHING IS SWALLOWED. This runs on a non-UI thread with nothing above it to catch, and
    /// an escaping exception would kill the process - the same defect class that made tray Exit
    /// crash the app before the callbacks above were marshalled. A tray icon that fails to come
    /// back is bad; taking the wallpaper engine down with it is worse.
    /// </remarks>
    private void RestoreAfterExplorerRestart()
    {
        if (_disposed) return;

        try
        {
            if (_icon.IsCreated)
            {
                try { _icon.Remove(); }
                catch (Exception) { }
            }

            _icon.Create();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Raises a Windows notification from the tray icon.</summary>
    /// <remarks>
    /// DELIBERATELY NOT MARSHALLED TO THE DISPATCHER, and that is the opposite of what the
    /// callbacks in the constructor do. This file already draws that distinction for
    /// RestoreAfterExplorerRestart: the callbacks marshal because they touch WPF objects, while
    /// anything touching the tray icon must run on the thread that owns its HWND, because
    /// Shell_NotifyIcon requires it. A notification is the second kind, so hopping to the
    /// Dispatcher would be the wrong move here, not the safe one.
    ///
    /// EVERYTHING IS SWALLOWED, for the same reason RestoreAfterExplorerRestart swallows: a
    /// notification that cannot be shown must never take down an import that actually worked. The
    /// worst case is a missing toast.
    ///
    /// respectQuietTime is true: an import finishing is useful, not urgent, and should not punch
    /// through Focus Assist.
    /// </remarks>
    public void Notify(string title, string message, bool failed)
    {
        if (_disposed) return;

        try
        {
            if (!_icon.IsCreated) return;

            _icon.ShowNotification(
                title,
                message,
                failed ? NotificationIcon.Error : NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: true,
                sound: true,
                respectQuietTime: true,
                realtime: false,
                timeout: null);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Before the icon: the handler closes over _icon, and a TaskbarCreated arriving mid-teardown
        // would otherwise try to re-create something that is being disposed.
        try { _taskbarCreated?.Dispose(); }
        catch (Exception) { }

        _icon.Dispose();
    }
}
