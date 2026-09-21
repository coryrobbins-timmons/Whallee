using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class PlayerSyncTests
{
    [Fact]
    public void DescribesPositionsForTheLog() =>
        Assert.Equal("1.42 / 0.88 / 0.00",
            PlayerSync.Describe(new double?[] { 1.42, 0.88, 0.0 }));

    /// <summary>
    /// A player with no position must read as "-", never as 0.00. Those mean opposite things: one is
    /// a player that never started, the other is a player sitting exactly at the beginning - which
    /// is what a successful seek looks like. Printing them the same would make the log lie about the
    /// very thing it was added to measure.
    /// </summary>
    [Fact]
    public void ShowsAMissingPositionAsADashNotAsZero() =>
        Assert.Equal("1.42 / - / 0.00",
            PlayerSync.Describe(new double?[] { 1.42, null, 0.0 }));

    [Fact]
    public void DescribesAnEmptySetWithoutThrowing() =>
        Assert.Equal("-", PlayerSync.Describe(Array.Empty<double?>()));

    /// <summary>
    /// A single display cannot be out of step with anything, and zero players is nothing to do.
    /// Sending a seek in either case would rewind a wallpaper for no reason at all.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void AlignsOnlyWhenThereIsSomethingToAlignWith(int playerCount, bool expected)
    {
        Assert.Equal(expected, PlayerSync.ShouldAlign(playerCount));
    }

    /// <summary>
    /// The relation that actually governs behaviour is PlayerSync.Timeout against
    /// MpvIpc.ConnectTimeoutMs, not against PollInterval (300 &gt; 15 can never break; there is no
    /// path that would make the poll interval approach the cap).
    ///
    /// Timeout must stay BELOW ConnectTimeoutMs, and that is deliberate, not merely a wide margin:
    /// AlignPlayers gives up waiting at Timeout, cancelling PlayerSync.AlignAsync's remaining sends,
    /// while a pipe connect already in flight can still take up to ConnectTimeoutMs to fail on its
    /// own. Cancellation is what makes that gap safe - it stops every send AFTER the one that is
    /// stuck, so the abandoned connect's eventual failure (up to 200 ms after the pass thread moved
    /// on, at today's values) can never trigger a later seek against a wallpaper that has already
    /// been revealed. Without cancellation, staying below ConnectTimeoutMs would be exactly backwards
    /// - the smaller cap would just mean giving up on the wait sooner while the background work kept
    /// running regardless. See PlayerSync.Timeout's own remarks for the full scenario.
    /// </summary>
    [Fact]
    public void TheTimeoutIsBelowTheConnectTimeout()
    {
        Assert.True(PlayerSync.Timeout < TimeSpan.FromMilliseconds(MpvIpc.ConnectTimeoutMs));
    }
}
