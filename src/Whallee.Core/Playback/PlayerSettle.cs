namespace Whallee.Core.Playback;

/// <summary>What the settle loop should do next.</summary>
public enum SettleStep
{
    /// <summary>Keep waiting.</summary>
    Wait,

    /// <summary>Every player is up and has opened its IPC pipe. Stop early.</summary>
    Ready,

    /// <summary>A player died. Stop now — the caller sweeps and reports.</summary>
    PlayerDied,

    /// <summary>The cap ran out. Stop and carry on with whatever is there.</summary>
    TimedOut
}

/// <summary>
/// The rule for how long to wait after launching players, before revealing them.
/// </summary>
/// <remarks>
/// REPLACES A FLAT ONE-SECOND SLEEP, which the pass profile showed was roughly half the cost of
/// applying a clip: a measured swap ran 2159 ms, of which liveness was 1001 ms.
///
/// The wait does two jobs and both have to survive the change:
///
/// 1. CATCH A PLAYER THAT DIES IMMEDIATELY — a corrupt or unplayable clip. Polling beats sleeping
///    here outright: a death is now noticed within a poll interval instead of always costing the
///    full second.
///
/// 2. GIVE mpv TIME TO RENDER BEFORE ITS WINDOW IS SHOWN. This is the one that matters visually.
///    A host window paints nothing until mpv draws into it, so revealing early shows Explorer's
///    wallpaper through it — exactly the flash that was fixed on 2026-09-08. mpv opening its IPC
///    pipe is the earliest pump-safe signal that it got as far as initialising; it is NOT proof of
///    a first frame, which is why a short grace follows it rather than revealing on the spot.
///
/// Pump-safe is a hard requirement, not a preference. This runs on the thread that owns windows
/// parented into Explorer's desktop, so the readiness test cannot be a blocking pipe connect -
/// MpvIpc.Send says as much in its own remarks. Checking that a named pipe EXISTS is a cheap
/// object-manager lookup; connecting to it is not.
/// </remarks>
public static class PlayerSettle
{
    /// <summary>Upper bound. Unchanged from the flat sleep it replaces, so the worst case is no worse.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(1);

    /// <summary>How often to re-check. Cheap enough to be frequent; 20 polls fill the cap.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Breathing room between "mpv has initialised" and showing its window.
    /// </summary>
    /// <remarks>
    /// The pipe appearing means mpv reached its init, not that a frame is on screen. This is the
    /// margin covering the gap. If the wallpaper flash ever comes back, THIS is the number to
    /// raise first - and the pass profile in the log is how you would see it.
    /// </remarks>
    public static readonly TimeSpan RenderGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Decides the next step. Pure — no clock, no processes, no pipes.
    /// </summary>
    /// <param name="anyPlayerDied">Whether any launched player has already exited.</param>
    /// <param name="allPipesUp">Whether every player has opened its IPC pipe.</param>
    /// <param name="elapsed">How long the loop has been waiting.</param>
    /// <param name="readySince">
    /// When every pipe first came up, or null if that has not happened yet. Ready is only returned
    /// once <see cref="RenderGrace"/> has passed since then.
    /// </param>
    public static SettleStep Decide(
        bool anyPlayerDied, bool allPipesUp, TimeSpan elapsed, TimeSpan? readySince)
    {
        // CHECKED FIRST, ahead of the cap. A death is the one outcome the caller must act on, and
        // reporting it as a timeout would lose the reason.
        if (anyPlayerDied) return SettleStep.PlayerDied;

        if (allPipesUp && readySince is { } since && elapsed - since >= RenderGrace)
            return SettleStep.Ready;

        return elapsed >= Cap ? SettleStep.TimedOut : SettleStep.Wait;
    }
}
