using Whallee.Core.Interop;
using static Whallee.Core.Interop.NativeConstants;

namespace Whallee.Core.Desktop;

public static class DesktopLayer
{
    /// <summary>
    /// Asks Explorer to spawn the wallpaper WorkerW, then locates it.
    /// Returns null if no WorkerW could be found after one retry.
    /// </summary>
    public static DesktopLayerInfo? Locate()
    {
        var progman = NativeMethods.FindWindowW("Progman", null);
        if (progman == 0)
            return null;

        // Ask Progman to create the wallpaper WorkerW. Undocumented but long-standing.
        NativeMethods.SendMessageTimeoutW(progman, WM_SPAWN_WORKER, 0xD, 0x1, SMTO_NORMAL, 1000, out _);

        var located = Find(progman);
        if (located is not null)
            return located;

        Thread.Sleep(500);   // Explorer is sometimes slow to create it
        return Find(progman);
    }

    private static DesktopLayerInfo? Find(nint progman)
    {
        var exStyle = (long)NativeMethods.GetWindowLongPtr(progman, GWL_EXSTYLE);
        var raised = (exStyle & WS_EX_NOREDIRECTIONBITMAP) != 0;

        if (raised)
        {
            var workerW = NativeMethods.FindWindowExW(progman, 0, "WorkerW", null);
            // Direct children of Progman in this layout - unlike the classic branch, where
            // WorkerW is a top-level sibling of the DefView owner.
            var defView = NativeMethods.FindWindowExW(progman, 0, "SHELLDLL_DefView", null);

            // Both handles are required. Task 8 passes ShellDefView as the SetWindowPos
            // insert-after target, and 0 there is HWND_TOP, not "not found" - propagating a
            // zero would silently place the wallpaper in front of the desktop icons.
            return workerW == 0 || defView == 0
                ? null
                : new DesktopLayerInfo(DesktopLayerMode.RaisedDesktop, progman, workerW, defView);
        }

        // Classic: find the top-level window that owns SHELLDLL_DefView, then take the WorkerW
        // that follows it in z-order. That sibling is the one painted behind the icons.
        nint owner = 0, defViewHandle = 0;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var defView = NativeMethods.FindWindowExW(hwnd, 0, "SHELLDLL_DefView", null);
            if (defView != 0)
            {
                owner = hwnd;
                defViewHandle = defView;
                return false; // stop enumerating
            }
            return true;
        }, 0);

        if (owner == 0)
            return null;

        var sibling = NativeMethods.FindWindowExW(0, owner, "WorkerW", null);
        return sibling == 0
            ? null
            : new DesktopLayerInfo(DesktopLayerMode.Classic, progman, sibling, defViewHandle);
    }
}
