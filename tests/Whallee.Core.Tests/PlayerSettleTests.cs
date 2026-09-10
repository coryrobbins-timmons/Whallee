using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class PlayerSettleTests
{
    private static readonly TimeSpan Grace = PlayerSettle.RenderGrace;
    private static readonly TimeSpan Cap = PlayerSettle.Cap;

    [Fact]
    public void WaitsWhileNothingHasHappenedYet()
    {
        var step = PlayerSettle.Decide(
            anyPlayerDied: false, allPipesUp: false,
            elapsed: TimeSpan.FromMilliseconds(50), readySince: null);

        Assert.Equal(SettleStep.Wait, step);
    }

    /// <summary>
    /// The whole point of polling: a pass that is ready early stops early instead of sleeping out
    /// a fixed second.
    /// </summary>
    [Fact]
    public void ReadyOncePipesHaveBeenUpForTheGracePeriod()
    {
        var readySince = TimeSpan.FromMilliseconds(150);

        var step = PlayerSettle.Decide(
            anyPlayerDied: false, allPipesUp: true,
            elapsed: readySince + Grace, readySince: readySince);

        Assert.Equal(SettleStep.Ready, step);
    }

    /// <summary>
    /// The grace exists because an open IPC pipe means mpv initialised, NOT that a frame is on
    /// screen. Revealing on the pipe alone brings back the wallpaper flash.
    /// </summary>
    [Fact]
    public void NotReadyImmediatelyWhenThePipesComeUp()
    {
        var readySince = TimeSpan.FromMilliseconds(150);

        var step = PlayerSettle.Decide(
            anyPlayerDied: false, allPipesUp: true,
            elapsed: readySince, readySince: readySince);

        Assert.Equal(SettleStep.Wait, step);
    }

    /// <summary>
    /// Checked ahead of the cap: reporting a death as a timeout would lose the reason the caller
    /// needs in order to report it.
    /// </summary>
    [Fact]
    public void ADeathWinsOverTheCap()
    {
        var step = PlayerSettle.Decide(
            anyPlayerDied: true, allPipesUp: false,
            elapsed: Cap + TimeSpan.FromSeconds(5), readySince: null);

        Assert.Equal(SettleStep.PlayerDied, step);
    }

    [Fact]
    public void ADeathWinsOverBeingReady()
    {
        var readySince = TimeSpan.FromMilliseconds(100);

        var step = PlayerSettle.Decide(
            anyPlayerDied: true, allPipesUp: true,
            elapsed: readySince + Grace, readySince: readySince);

        Assert.Equal(SettleStep.PlayerDied, step);
    }

    /// <summary>
    /// A player whose pipe never appears must not hang the pass; the cap is the same one second the
    /// flat sleep used, so the worst case is no worse than before.
    /// </summary>
    [Fact]
    public void TimesOutWhenThePipesNeverComeUp()
    {
        var step = PlayerSettle.Decide(
            anyPlayerDied: false, allPipesUp: false, elapsed: Cap, readySince: null);

        Assert.Equal(SettleStep.TimedOut, step);
    }

    /// <summary>
    /// Pipes up late enough that the grace would run past the cap: the cap still ends the wait, so
    /// the grace can never extend a pass beyond its bound.
    /// </summary>
    [Fact]
    public void TheCapBoundsTheGraceToo()
    {
        var readySince = Cap - TimeSpan.FromMilliseconds(10);

        var step = PlayerSettle.Decide(
            anyPlayerDied: false, allPipesUp: true, elapsed: Cap, readySince: readySince);

        Assert.Equal(SettleStep.TimedOut, step);
    }

    [Fact]
    public void TheCapIsNoLongerThanTheFlatSleepItReplaced()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), Cap);
        Assert.True(Grace < Cap, "the grace must fit inside the cap");
        Assert.True(PlayerSettle.PollInterval < Grace, "polling must be finer than the grace");
    }
}
