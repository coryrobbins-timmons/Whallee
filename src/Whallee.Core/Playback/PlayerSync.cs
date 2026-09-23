using System.Diagnostics;
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
    /// The cap on how long Phase 1 of <see cref="AlignAsync"/> will wait for the players to start
    /// playing, enforced by AlignAsync itself.
    /// </summary>
    /// <remarks>
    /// 1000 ms, and reasoned rather than picked. One round of three time-pos queries costs about
    /// 120 ms, so this cap is comfortably larger than several rounds of that to be a ceiling rather
    /// than the common path - the old 300 ms value was smaller than a single round, which is why
    /// every sync timed out on 2026-09-21. The loop exits the moment the players are ready, so a
    /// healthy pass does not pay this.
    ///
    /// COVERS PHASE 1 ONLY, AND ENFORCED FROM INSIDE AlignAsync, NOT MERELY BY THE CALLER GIVING UP.
    /// It used to rely entirely on WallpaperEngine.AlignPlayers abandoning its own wait at this same
    /// value, which left two problems: nothing stopped Phase 1's loop from running past this cap on
    /// its own if the caller's wait were ever changed independently, and - the one that actually
    /// bit, see BurstAllowance below - the pause/seek/resume/verify burst that follows Phase 1 had no
    /// budget of its own at all, so a Phase 1 that finished late could still be mid-burst when the
    /// caller's identical cap elapsed. AlignAsync now bounds its own wait to this value; the caller
    /// waits this value PLUS BurstAllowance, so the burst always has room to finish before the reveal.
    ///
    /// THE PREVIOUS INVARIANT HERE NO LONGER APPLIES. This value used to be deliberately BELOW
    /// MpvIpc.ConnectTimeoutMs so that abandoning the wait could cancel seeks before they landed
    /// after the reveal. That was correct for a step that only seeked. It is wrong for a step that
    /// pauses: cancelling between a pause and its resume is exactly how a display gets frozen. The
    /// cancellable part is now only the WAIT; see AlignAsync.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// How much EXTRA time the caller should allow, beyond <see cref="Timeout"/>, for the
    /// pause/seek/resume/verify burst (Phases 2-5) to finish before it gives up and reveals anyway.
    /// </summary>
    /// <remarks>
    /// DERIVED THE SAME WAY Timeout IS: reasoned with headroom, not measured to the millisecond. The
    /// burst is three rounds of MpvIpc.Send (pause, seek, resume) plus one verification round that is
    /// a QUERY rather than a send, and so carries MpvIpc.ReadTimeoutMs's cost rather than
    /// MpvIpc.ConnectTimeoutMs's; a stalled player can also earn one more resume attempt on top of
    /// that. Budgeting three query-sized rounds (3 * MpvIpc.ReadTimeoutMs) covers the verification
    /// round with headroom to spare for that retry, and three send-sized rounds
    /// (3 * MpvIpc.ConnectTimeoutMs) covers pause/seek/resume even in the worst case of a player that
    /// has just become unreachable and makes each Send wait out its full connect timeout. A real
    /// burst has measured at roughly 250 ms; this constant is comfortably above that on purpose, the
    /// same way Timeout is comfortably above a healthy Phase 1.
    ///
    /// WHY THIS MUST BE ADDED TO Timeout, NOT USED IN ITS PLACE. Phase 1 and the burst are sequential,
    /// not overlapping - the burst only starts once Phase 1 has succeeded - so the caller's total
    /// wait has to cover both in full. A cap that only covered Timeout let the caller abandon at
    /// exactly the moment a late-but-successful Phase 1 handed off to the burst, which then ran in
    /// full view after RevealPlayers had already shown the windows - a visible freeze/jump, and
    /// exactly what this constant exists to stop.
    /// </remarks>
    public static readonly TimeSpan BurstAllowance = TimeSpan.FromMilliseconds(
        3 * MpvIpc.ReadTimeoutMs + 3 * MpvIpc.ConnectTimeoutMs);

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
    /// Waits for every player to start playing, then brings them into step: pause, seek to zero,
    /// resume together. Never throws.
    /// </summary>
    /// <remarks>
    /// THREE PHASES, AND THE BOUNDARY BETWEEN THEM IS THE SAFETY PROPERTY OF THIS WHOLE DESIGN.
    ///
    /// Phase 1, waiting, is cancellable: nothing has been touched, so giving up costs only the
    /// alignment - which is what the pass did before any of this existed.
    ///
    /// Phases 2 to 4 - pause, seek, resume - are NOT cancellable once begun. A cancellation between
    /// a pause and its resume leaves that display frozen on a single frame indefinitely, which is
    /// far worse than the stagger being fixed. The finally block resumes everything that was paused
    /// on every exit path including an exception, and a resume is idempotent, so resuming twice is
    /// harmless while missing one is not.
    ///
    /// ONLY PLAYERS WHOSE PAUSE WAS ACCEPTED ARE TOUCHED FURTHER. MpvIpc.Send returns false for a
    /// player it could not reach, and such a player is never added to the paused set - so it is
    /// never seeked, never resumed, and keeps playing exactly as it does today. This is why the
    /// pause is applied HERE rather than as --pause on the command line: a player launched paused
    /// whose IPC pipe never binds could never be freed, and this repo already records that pipe
    /// binding silently failing (see WallpaperEngine.PipeNameFor).
    /// </remarks>
    public static Task<ReleaseReport> AlignAsync(
        IReadOnlyList<string> pipeNames, double?[] observed, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            // PHASE 1 - WAIT UNTIL EVERY PLAYER IS PLAYING. Cancellable, AND now bounded by Timeout
            // ON ITS OWN, rather than trusting the caller to enforce that externally. Previously this
            // loop had no cap of its own at all - it ran until either AllPlaying went true or the
            // caller's cancellationToken fired, and WallpaperEngine.AlignPlayers was the only thing
            // that ever set that cap, at exactly Timeout. That worked by coincidence, not by design:
            // this method should not rely entirely on caller cancellation to stop wasted work once
            // its own budget is spent, and a future caller that waited longer (as this one now does,
            // by BurstAllowance - see Timeout's remarks) would otherwise let Phase 1 run unbounded.
            var ready = false;
            var waitClock = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested && waitClock.Elapsed < Timeout)
            {
                for (var i = 0; i < pipeNames.Count && i < observed.Length; i++)
                    observed[i] = await MpvIpc.QueryTimePosAsync(pipeNames[i], cancellationToken)
                        .ConfigureAwait(false);

                if (AllPlaying(observed)) { ready = true; break; }

                // A round of queries against an unanswering player already costs
                // MpvIpc.ReadTimeoutMs each, so this is a courtesy rather than the pacing.
                try { await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            if (!ready) return new ReleaseReport(false, 0, 0, 0, 0);

            // ONE LAST CANCELLATION CHECK, RIGHT HERE, AND NOWHERE FURTHER IN. This is the last
            // moment at which giving up is free: nothing has been paused yet, so there is no
            // paused player to leave stranded. The cap can elapse while Phase 1's loop is still
            // mid-iteration, or in the gap between AllPlaying going true and this line running;
            // without this check that race lets the pause/seek/resume burst start AFTER
            // AlignPlayers has already given up and RevealPlayers has shown the windows, so the
            // user would see the displays freeze, jump to zero and restart in full view. Once
            // Phase 2 begins below, cancellation is deliberately no longer observed - see the
            // remarks on this method and on WallpaperEngine.AlignPlayers's CancellationTokenSource
            // for why that boundary is correct.
            if (cancellationToken.IsCancellationRequested) return new ReleaseReport(false, 0, 0, 0, 0);

            var paused = new List<string>(pipeNames.Count);
            var seeked = 0;
            var resumed = 0;
            var retried = 0;
            try
            {
                // PHASE 2 - PAUSE, remembering exactly who accepted it.
                foreach (var pipeName in pipeNames)
                    if (MpvIpc.Send(pipeName, MpvIpc.PauseCommand(true))) paused.Add(pipeName);

                // PHASE 3 - SEEK. Paused players hold position, so this is what closes the gap.
                foreach (var pipeName in paused)
                    if (MpvIpc.Send(pipeName, MpvIpc.SeekToStartCommand)) seeked++;

                // PHASE 4 - RESUME, back to back. About 5 ms apart instead of the ~300 ms that
                // staggered launch produced.
                foreach (var pipeName in paused)
                    if (MpvIpc.Send(pipeName, MpvIpc.PauseCommand(false))) resumed++;

                // PHASE 5 - VERIFY. A running player has moved off zero; one still at zero did not
                // take the resume. Give those one more attempt - a frozen display is the failure
                // this design exists to avoid, and one retry is cheap insurance against it.
                var after = new double?[paused.Count];
                for (var i = 0; i < paused.Count; i++)
                    after[i] = await MpvIpc.QueryTimePosAsync(paused[i], CancellationToken.None)
                        .ConfigureAwait(false);

                foreach (var index in StalledIndexes(after))
                {
                    MpvIpc.Send(paused[index], MpvIpc.PauseCommand(false));
                    retried++;
                }
            }
            finally
            {
                // BELT AND BRACES, AND NOT REDUNDANT. Phase 4 may have been interrupted by an
                // exception, or a resume may simply not have landed. Resuming is idempotent; a
                // display left paused is not recoverable without another pass.
                foreach (var pipeName in paused)
                    MpvIpc.Send(pipeName, MpvIpc.PauseCommand(false));
            }

            return new ReleaseReport(true, paused.Count, seeked, resumed, retried);
        });

    /// <summary>Renders a set of positions for the log: "1.42 / 0.88 / -" with "-" for no answer.</summary>
    public static string Describe(IReadOnlyList<double?> positions) =>
        positions.Count == 0
            ? "-"
            : string.Join(" / ", positions.Select(p =>
                p is { } seconds ? seconds.ToString("F2", CultureInfo.InvariantCulture) : "-"));
}

/// <summary>What one alignment attempt did.</summary>
/// <param name="Ready">Whether every player reported playing before the cap elapsed.</param>
/// <param name="Paused">How many players accepted a pause, and so were seeked and resumed.</param>
/// <param name="Seeked">How many accepted the seek.</param>
/// <param name="Resumed">How many accepted the resume.</param>
/// <param name="Retried">How many needed a second resume after verification.</param>
public readonly record struct ReleaseReport(
    bool Ready, int Paused, int Seeked, int Resumed, int Retried);

