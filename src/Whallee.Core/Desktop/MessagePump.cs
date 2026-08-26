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
}
