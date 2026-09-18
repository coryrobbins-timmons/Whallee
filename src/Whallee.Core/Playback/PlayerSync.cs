namespace Whallee.Core.Playback;

/// <summary>
/// Brings every player back to the same position, once, after they have all started.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The players are launched back to back in a loop - a real swap profile recorded
/// "launchMpv 940" for three displays, roughly 300 ms apart each - and then each loops on its own
/// clock. Whatever offset they start with is PERMANENT, because nothing ever realigns them.
///
/// That was invisible while the displays showed different clips. Auto-apply-on-import (2026-09-11)
/// made same-clip-everywhere the normal state after every download, so all three monitors now
/// usually play the same video, staggered by up to two thirds of a second. The owner reported it on
/// 2026-09-16.
///
/// THE STAGGERED START IS THE ENTIRE ERROR. Two mpv instances playing the same file are clocked off
/// the same timebase at the same rate, so once aligned they stay aligned. This is NOT a drift
/// problem and must not be turned into one: no timer, no periodic re-sync, no watching playback
/// position. One seek, once, and then leave them alone.
/// </remarks>
public static class PlayerSync
{
    /// <summary>How often the caller should poll while waiting for a sync to finish.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// The cap on that wait. Timing out is NOT an error: it means the wallpaper appears with the
    /// players possibly unaligned, which is what happened before this class existed.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Whether a set of this size is worth aligning at all.</summary>
    public static bool ShouldAlign(int playerCount) => playerCount >= 2;

    /// <summary>
    /// Seeks every player to the start. Never throws.
    /// </summary>
    /// <remarks>
    /// RUNS OFF THE CALLING THREAD, and that is the whole reason this returns a Task rather than
    /// doing the work inline. MpvIpc.Send BLOCKS on a named-pipe connect with a 500 ms timeout, and
    /// the pass thread owns windows re-parented into Explorer's desktop - three unreachable pipes
    /// inline would be a second and a half of frozen desktop.
    ///
    /// Sent one after another rather than in parallel. A pipe write is microseconds once the pipe
    /// is up, and the settle step has already waited for every player's IPC pipe to exist, so the
    /// sends land within a hair of each other. Parallel tasks would buy nothing measurable and cost
    /// three threads.
    ///
    /// Failures are ignored on purpose: MpvIpc.Send returns false rather than throwing, and a
    /// player that cannot be reached simply carries on playing where it was - the behaviour this
    /// class replaced.
    /// </remarks>
    public static Task AlignAsync(IReadOnlyList<string> pipeNames) => Task.Run(() =>
    {
        foreach (var pipeName in pipeNames)
            MpvIpc.Send(pipeName, MpvIpc.SeekToStartCommand);
    });
}
