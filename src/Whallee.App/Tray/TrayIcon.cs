using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon.Core;

namespace Whallee.App.Tray;

/// <summary>Tray presence for the app. The menu grows in Task 11 (Next clip, Pause/Resume).</summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>MAKEINTRESOURCE(32512) — the stock application icon.</summary>
    private const int IdiApplication = 32512;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

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

            // An explicit icon, not the default 0 handle. Shell_NotifyIcon with no icon leaves the
            // shell nothing to draw, so the tray entry can be invisible or a blank gap - which
            // would make the owner's "does an icon appear?" check unanswerable. Stock icon now;
            // Task 13 swaps in the real one. LoadIconW on a stock id returns a SHARED handle that
            // must never be destroyed, which is why nothing here frees it.
            Icon = LoadIconW(0, IdiApplication)
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
