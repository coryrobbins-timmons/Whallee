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
    /// <remarks>
    /// DELIBERATELY BELOW <see cref="MpvIpc.ConnectTimeoutMs"/> (500 ms), not above it. Raising this
    /// past 500 ms would let every pass wait a full extra 200 ms for the wallpaper to appear, just
    /// to accommodate the rare pipe that never comes up - a bad trade for something that already
    /// degrades safely.
    ///
    /// What makes staying below it safe rather than merely faster is cancellation:
    /// <see cref="AlignAsync"/> takes a <see cref="CancellationToken"/> and
    /// WallpaperEngine.AlignPlayers cancels it the moment this cap elapses, which stops every send
    /// AFTER whichever one is currently blocked inside <see cref="MpvIpc.Send"/>'s named-pipe
    /// connect. Without that cancellation, a single slow pipe would let the pass thread give up here
    /// at 300 ms while the background task kept running for up to another
    /// <see cref="MpvIpc.ConnectTimeoutMs"/> per remaining pipe - and land seeks against a wallpaper
    /// that had already been revealed. That was worse than the bug this class exists to fix: a
    /// silent constant offset became a visible multi-display rewind.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Whether a set of this size is worth aligning at all.</summary>
    public static bool ShouldAlign(int playerCount) => playerCount >= 2;

    /// <summary>
    /// Seeks every player to the start. Never throws.
    /// </summary>
    /// <param name="pipeNames">
    /// The players to seek. THE CALLER FILTERS THIS, not this method - it is expected to already be
    /// narrowed to pipes confirmed up (<see cref="MpvHost.IsIpcPipeUp"/>) at send time. Do not treat
    /// "the settle step waited for pipes" as proof they are still all up by the time this runs:
    /// settle's PlayerDied and TimedOut exits (see PlayerSettle.Decide) both reach the caller without
    /// every pipe having been confirmed - a died player never reached allPipesUp, and a timeout by
    /// definition means the cap ran out with at least one pipe still down.
    /// </param>
    /// <param name="cancellationToken">
    /// Checked BETWEEN sends, never during one - <see cref="MpvIpc.Send"/>'s blocking named-pipe
    /// connect is not interruptible, so a send already in flight always runs to its own completion or
    /// its own <see cref="MpvIpc.ConnectTimeoutMs"/>. What cancellation buys is stopping every send
    /// AFTER that one: <see cref="WallpaperEngine.AlignPlayers"/> cancels this the moment its
    /// <see cref="Timeout"/> cap elapses, which is what stops an abandoned pipe's eventual connect
    /// failure from letting a LATER pipe's seek land after the wallpaper has already been revealed.
    /// See the remarks on <see cref="Timeout"/> for the full failure mode this closes.
    /// </param>
    /// <remarks>
    /// RUNS OFF THE CALLING THREAD, and that is the whole reason this returns a Task rather than
    /// doing the work inline. MpvIpc.Send BLOCKS on a named-pipe connect with a
    /// <see cref="MpvIpc.ConnectTimeoutMs"/> timeout, and the pass thread owns windows re-parented
    /// into Explorer's desktop - three unreachable pipes inline would be a second and a half of
    /// frozen desktop.
    ///
    /// Sent one after another rather than in parallel. A pipe write is microseconds once the pipe
    /// is up, so for the common case - every pipe genuinely up - the sends land within a hair of
    /// each other regardless. Parallel tasks would buy nothing measurable there and would cost three
    /// threads, and sequential is also what makes per-send cancellation meaningful at all: a
    /// parallel fan-out has no "remaining sends" left to cancel once every task is already running.
    ///
    /// Failures are ignored on purpose: MpvIpc.Send returns false rather than throwing, and a
    /// player that cannot be reached simply carries on playing where it was - the behaviour this
    /// class replaced.
    /// </remarks>
    /// <returns>
    /// How many players accepted the seek.
    /// </returns>
    /// <remarks>
    /// THE COUNT IS DIAGNOSTIC, added 2026-09-21. The owner reported two displays aligning and a
    /// third lagging behind, and the log could not tell "the third seek was never sent" from "it was
    /// sent and did nothing" - two symptoms with completely different fixes. MpvIpc.Send has always
    /// returned that answer; this method used to discard it.
    /// </remarks>
    public static Task<int> AlignAsync(IReadOnlyList<string> pipeNames, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var sent = 0;
            foreach (var pipeName in pipeNames)
            {
                // Between sends only - see the cancellationToken remarks above for why that is the
                // right granularity and the most that is achievable here.
                if (cancellationToken.IsCancellationRequested) return sent;
                if (MpvIpc.Send(pipeName, MpvIpc.SeekToStartCommand)) sent++;
            }

            return sent;
        });
}
