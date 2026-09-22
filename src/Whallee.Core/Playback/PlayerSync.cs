using System.Globalization;
using System.Linq;

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
    /// The cap on how long the pass will wait for the players to start playing.
    /// </summary>
    /// <remarks>
    /// 1000 ms, and reasoned rather than picked. One round of three time-pos queries costs about
    /// 120 ms; the pause/seek/resume burst plus verification costs roughly another 250 ms. The cap
    /// has to be comfortably larger than those to be a ceiling rather than the common path - the
    /// old 300 ms value was smaller than the work itself, which is why every sync timed out on
    /// 2026-09-21. The loop exits the moment the players are ready, so a healthy pass does not pay
    /// this.
    ///
    /// THE PREVIOUS INVARIANT HERE NO LONGER APPLIES. This value used to be deliberately BELOW
    /// MpvIpc.ConnectTimeoutMs so that abandoning the wait could cancel seeks before they landed
    /// after the reveal. That was correct for a step that only seeked. It is wrong for a step that
    /// pauses: cancelling between a pause and its resume is exactly how a display gets frozen. The
    /// cancellable part is now only the WAIT; see AlignAsync.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>Whether a set of this size is worth aligning at all.</summary>
    public static bool ShouldAlign(int playerCount) => playerCount >= 2;

    /// <summary>Whether every player has reported a playback position.</summary>
    /// <remarks>
    /// THIS IS THE CONDITION THAT WAS MISSING, and the whole reason the previous mechanism achieved
    /// nothing. Measured on 2026-09-21: at the moment the players were seeked their positions were
    /// "0.23 / 0.00 / 0.00", and twice "0.17 / - / 0.00" - a dash being a player that answered
    /// nothing because it had not begun playing. They were not apart in POSITION, so seeking them
    /// all to zero was a no-op; they were apart in WHEN THEY START, which happens after the pass's
    /// sync step used to run.
    ///
    /// Zero counts as playing. It is a real position, and it is what a player reports in the instant
    /// after it starts; treating it as "not yet" would stall the wait against a healthy player.
    ///
    /// An empty set is ready. There is nothing to wait for, and returning false would spin the
    /// caller's loop to its cap for no reason.
    /// </remarks>
    public static bool AllPlaying(IReadOnlyList<double?> positions) =>
        positions.All(p => p is not null);

    /// <summary>
    /// Which players did not take their resume, by index into the set that was paused.
    /// </summary>
    /// <remarks>
    /// A RUNNING PLAYER HAS MOVED OFF ZERO. One still reporting exactly 0.00, or reporting nothing
    /// at all, did not resume - and a player left paused is a display frozen on a single frame
    /// indefinitely, which is the worst outcome this design can produce. Those get one more attempt.
    ///
    /// Note the asymmetry with AllPlaying, which treats zero as fine: there, zero means "has
    /// started"; here, after a seek to zero and a resume, zero means "has not moved".
    /// </remarks>
    public static IReadOnlyList<int> StalledIndexes(IReadOnlyList<double?> positions)
    {
        var stalled = new List<int>();
        for (var i = 0; i < positions.Count; i++)
            if (positions[i] is not { } seconds || seconds <= 0) stalled.Add(i);

        return stalled;
    }

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
    /// What happened: how many players accepted the seek, and where each one reported being
    /// immediately before and immediately after it.
    /// </returns>
    /// <remarks>
    /// THE MEASUREMENTS ARE DIAGNOSTIC, added 2026-09-21. The owner reported displays out of step;
    /// the log then proved every seek was delivered and accepted, and the displays were still out of
    /// step. At that point the only unanswered question was whether the players actually MOVED, and
    /// this class could not see - MpvIpc could only write. The before/after positions answer it
    /// directly: identical before-values mean the players were never apart in position and seeking
    /// was the wrong mechanism; non-zero after-values mean the seek was accepted and ignored.
    ///
    /// STILL WRAPPED IN Task.Run, and that is not redundant now the body is async. The caller is the
    /// pass thread, which owns windows re-parented into Explorer's desktop AND carries a WPF
    /// SynchronizationContext. Without Task.Run the method would run synchronously up to its first
    /// await, and every continuation would be posted BACK to that thread - putting blocking pipe I/O
    /// on the one thread in this process where blocking freezes the user's whole desktop.
    /// </remarks>
    public static Task<int> AlignAsync(
        IReadOnlyList<string> pipeNames, double?[] before, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            // BEFORE-VALUES ONLY, not before AND after. Measuring both was tried on 2026-09-21 and
            // cost about 255 ms against this method's 300 ms budget, so every sync timed out and the
            // players stopped being aligned at all - a diagnostic that broke the thing it was
            // measuring. The before-values are also the discriminating ones on their own: if the
            // players are already all at zero when we get here, they were never apart in POSITION,
            // and seeking them cannot be what fixes a stagger.
            for (var i = 0; i < pipeNames.Count && i < before.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) return 0;
                before[i] = await MpvIpc.QueryTimePosAsync(pipeNames[i], cancellationToken)
                    .ConfigureAwait(false);
            }

            var sent = 0;
            for (var i = 0; i < pipeNames.Count; i++)
            {
                // Between sends only - see the cancellationToken remarks above for why that is the
                // right granularity and the most that is achievable here.
                if (cancellationToken.IsCancellationRequested) return sent;
                if (MpvIpc.Send(pipeNames[i], MpvIpc.SeekToStartCommand)) sent++;
            }

            return sent;
        });

    /// <summary>Renders a set of positions for the log: "1.42 / 0.88 / -" with "-" for no answer.</summary>
    public static string Describe(IReadOnlyList<double?> positions) =>
        positions.Count == 0
            ? "-"
            : string.Join(" / ", positions.Select(p =>
                p is { } seconds ? seconds.ToString("F2", CultureInfo.InvariantCulture) : "-"));
}

