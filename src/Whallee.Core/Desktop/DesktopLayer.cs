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
        //
        // This is a *send*, not a post: it blocks this thread for up to 1 s waiting on Explorer's
        // desktop thread, without draining our own posted input. That is safe only because of a
        // dependency that is load-bearing and otherwise invisible: every caller reaches Locate()
        // either before any host window exists (first Start()) or after StopAll() has destroyed
        // them all (Restart()), so at this instant no SetParent attachment binds our input queue
        // to Explorer's. If a future change ever calls Locate() while host windows are attached,
        // this line becomes a 1 s freeze of the user's whole desktop — the fifth instance of the
        // bug class this branch has already fought four times.
        //
        // AMENDED 2026-09-08. WallpaperEngine.SwapOnce keeps the outgoing host windows alive on
        // purpose while it builds their replacements, which is precisely the forbidden state. It
        // therefore does not call Locate at all — it reuses the handle the last hard pass cached,
        // having first checked with IsWindow that Explorer has not destroyed it. There is still
        // exactly ONE call site in the codebase (StartOnce, immediately after StopAll). Keep it
        // that way: if a path that already owns attached windows needs the desktop layer, cache
        // it, do not look it up.
        //
        // SMTO_ABORTIFHUNG at least bounds it to "give up immediately" when Explorer is already
        // hung, rather than waiting out the full timeout behind a shell that is never going to
        // answer.
        NativeMethods.SendMessageTimeoutW(
            progman, WM_SPAWN_WORKER, 0xD, 0x1, SMTO_NORMAL | SMTO_ABORTIFHUNG, 1000, out _);

        var located = Find(progman);
        if (located is not null)
            return located;

        // Explorer is sometimes slow to create it. AMENDED 2026-08-27: must pump while waiting,
        // not Thread.Sleep - Task 13 makes Locate() reachable from Restart() on the display-change
        // path, where Explorer is alive and our input queue may already be attached to its
        // desktop thread via SetParent. A plain sleep there would stall the user's desktop input
        // for the duration.
        MessagePump.Sleep(TimeSpan.FromMilliseconds(500));
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
