using System.Diagnostics;
using System.Runtime.InteropServices;
using Whallee.Core.Interop;
using static Whallee.Core.Interop.NativeConstants;

namespace Whallee.Core.Desktop;

/// <summary>
/// Pumps the calling thread's message queue.
/// </summary>
/// <remarks>
/// This is mandatory, not decorative. <see cref="HostWindow"/> uses SetParent to move a window
/// this process owns into Explorer's desktop hierarchy, and cross-thread parenting attaches the
/// two threads' input queues — from then on they process input in lockstep. A thread that owns a
/// host window and stops pumping therefore stalls Explorer's desktop as well: desktop icons stop
/// responding to clicks and the wait cursor appears across the screen. That is exactly what a
/// bare <c>Console.ReadLine()</c> produced before this type existed.
///
/// The corollary binds every caller: never run slow or blocking work on a thread that owns a
/// host window. Hand it to a background task and pump here instead.
/// </remarks>
public static class MessagePump
{
    /// <summary>
    /// Pumps messages until <paramref name="until"/> completes, then returns. Must be called on
    /// the same thread that created the host window(s), and that thread must reach this call
    /// promptly after creating them.
    /// </summary>
    public static void RunUntil(Task until)
    {
        var threadId = NativeMethods.GetCurrentThreadId();

        // GetMessageW blocks in the kernel until *some* message arrives, so the task completing
        // is not by itself enough to wake it — post WM_QUIT to break the wait. If the task has
        // already completed, this continuation runs inline right here and the message is sitting
        // in the queue before the loop starts. Faults and cancellation wake it too, deliberately:
        // any exit of the reader must release the pump so the caller can tear down.
        //
        // NOTE for any future nested pump, anywhere: this is an ORDINARY queued WM_QUIT, not the
        // sticky quit flag PostQuitMessage sets. A nested PeekMessageW(PM_REMOVE) loop will
        // therefore consume it, and TranslateMessage/DispatchMessageW on a WM_QUIT whose hwnd is 0
        // silently do nothing — so the message is destroyed and the loop below blocks forever.
        // Every nested pump must detect WM_QUIT and re-post it rather than dispatch it. The one
        // correct way to drain from a nested wait is DrainQueue below; use it.
        _ = until.ContinueWith(
            _ => NativeMethods.PostThreadMessageW(threadId, WM_QUIT, 0, 0),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        int result;
        while ((result = NativeMethods.GetMessageW(out var message, 0, 0, 0)) != 0)
        {
            if (result < 0)
                throw new InvalidOperationException(
                    $"GetMessageW failed: {Marshal.GetLastWin32Error()}");

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }
    }

    /// <summary>
    /// A drop-in replacement for <c>Process.WaitForExit(int)</c> for any caller that may own host
    /// windows: waits up to <paramref name="timeout"/> for <paramref name="process"/> to exit,
    /// pumping the calling thread's message queue while it waits. Returns true if the process
    /// exited within the window, false on timeout.
    /// </summary>
    public static bool WaitForExit(Process process, TimeSpan timeout)
    {
        var handles = new[] { process.Handle };
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            var remainingMs = remaining <= TimeSpan.Zero
                ? 0u
                : (uint)Math.Min(remaining.TotalMilliseconds, int.MaxValue);

            var waitResult = NativeMethods.MsgWaitForMultipleObjectsEx(
                1, handles, remainingMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

            switch (waitResult)
            {
                case 0: // WAIT_OBJECT_0: the process handle is signaled
                    return true;

                case 1: // WAIT_OBJECT_0 + 1: new input is queued - drain it, then re-wait
                    // A WM_QUIT during the drain means RunUntil is tearing this thread down.
                    // Report "did not exit": we are shutting down, and claiming a healthy mpv
                    // avoids a spurious "mpv failed" exception on the way out.
                    if (DrainQueue())
                        return false;
                    continue;

                case WAIT_TIMEOUT:
                    return false;

                default: // WAIT_FAILED and anything else unexpected
                    throw new InvalidOperationException(
                        $"MsgWaitForMultipleObjectsEx failed: {Marshal.GetLastWin32Error()}");
            }
        }
    }

    /// <summary>
    /// A pumping equivalent of <c>Thread.Sleep(TimeSpan)</c> for any caller that may own host
    /// windows: waits out <paramref name="duration"/> while dispatching messages, rather than
    /// blocking the thread outright.
    /// </summary>
    /// <remarks>
    /// AMENDED 2026-08-27: added because <see cref="Whallee.Core.Desktop.DesktopLayer"/>.Locate()
    /// had a plain <c>Thread.Sleep(500)</c> retry delay - harmless while nothing had reparented a
    /// window yet, but Task 13 makes <c>Restart()</c> reachable from inside a dispatched
    /// <c>WndProc</c> message, by which point our input queue may already be attached to
    /// Explorer's. A non-pumping sleep there is a genuine 500 ms stall of the user's desktop.
    ///
    /// Built on the same <c>MsgWaitForMultipleObjectsEx</c> machinery as <see cref="WaitForExit"/>,
    /// but with zero handles: the return-value mapping differs. With <c>nCount = 0</c>, "new input
    /// is queued" is signaled by <c>WAIT_OBJECT_0 + nCount</c>, i.e. <c>0</c> - the same value
    /// <see cref="WaitForExit"/> uses for "the handle is signaled". Here <c>0</c> instead means
    /// drain the queue and re-wait; only <c>WAIT_TIMEOUT</c> (or a <c>WM_QUIT</c> seen while
    /// draining) ends the wait. The two mappings are deliberately NOT unified — the same return
    /// value means opposite things at <c>nCount = 1</c> and <c>nCount = 0</c>.
    /// </remarks>
    public static void Sleep(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            var remainingMs = remaining <= TimeSpan.Zero
                ? 0u
                : (uint)Math.Min(remaining.TotalMilliseconds, int.MaxValue);

            var waitResult = NativeMethods.MsgWaitForMultipleObjectsEx(
                0, null, remainingMs, QS_ALLINPUT, MWMO_INPUTAVAILABLE);

            switch (waitResult)
            {
                case WAIT_TIMEOUT:
                    return;

                case 0: // WAIT_OBJECT_0 + nCount here (nCount = 0): input is queued
                    // A WM_QUIT during the drain means RunUntil is tearing this thread down:
                    // stop waiting out the remaining duration and unwind.
                    if (DrainQueue())
                        return;
                    continue;

                default: // WAIT_FAILED and anything else unexpected
                    // Deliberately consistent with WaitForExit's default arm: an unrecognised
                    // wait code is an error, not "input is available". Treating it as input
                    // would spin this loop until the deadline against a broken wait.
                    throw new InvalidOperationException(
                        $"MsgWaitForMultipleObjectsEx failed: {Marshal.GetLastWin32Error()}");
            }
        }
    }

    /// <summary>
    /// Dispatches every message currently queued on the calling thread. Returns true if a
    /// <c>WM_QUIT</c> was found — in which case it has been re-posted to this thread and draining
    /// stopped, and the caller must unwind rather than keep waiting.
    /// </summary>
    /// <remarks>
    /// The re-post is the whole point. <see cref="RunUntil"/> signals shutdown with
    /// <c>PostThreadMessageW(WM_QUIT)</c>, an ordinary queued message rather than
    /// <c>PostQuitMessage</c>'s sticky flag, and dispatching a <c>WM_QUIT</c> whose <c>hwnd</c> is
    /// 0 silently does nothing. A nested drain that simply dispatched it would therefore consume
    /// the only shutdown signal in existence, leaving <see cref="RunUntil"/>'s <c>GetMessageW</c>
    /// blocked forever: the harness would never exit and the wallpapers would stay up.
    /// </remarks>
    private static bool DrainQueue()
    {
        while (NativeMethods.PeekMessageW(out var message, 0, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT)
            {
                NativeMethods.PostThreadMessageW(NativeMethods.GetCurrentThreadId(), WM_QUIT, 0, 0);
                return true;
            }

            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessageW(ref message);
        }

        return false;
    }
}
