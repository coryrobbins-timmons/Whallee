using System.Runtime.InteropServices;
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

    public TrayIcon(Action onShow, Action onExit)
    {
        var menu = new PopupMenu();
        menu.Items.Add(new PopupMenuItem("Show Whallee", (_, _) => onShow()));
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem("Exit", (_, _) => onExit()));

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
            if (args.MouseEvent == MouseEvent.IconDoubleClick) onShow();
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
