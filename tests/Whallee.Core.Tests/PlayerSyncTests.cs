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
    /// THE CONDITION THE 2026-09-21 MEASUREMENTS PROVED ABSENT. The log recorded positions of
    /// "0.23 / 0.00 / 0.00" and twice "0.17 / - / 0.00" at the moment the players were seeked: a
    /// dash means that player reported no position at all, because it had not begun playing. A
    /// player with no position is not ready, and seeking it achieves nothing.
    /// </summary>
    [Fact]
    public void EveryPlayerReportingAPositionIsReady() =>
        Assert.True(PlayerSync.AllPlaying(new double?[] { 0.23, 0.01, 0.40 }));

    [Fact]
    public void APlayerWithNoPositionIsNotReady() =>
        Assert.False(PlayerSync.AllPlaying(new double?[] { 0.23, null, 0.40 }));

    /// <summary>
    /// Zero is a real position - a player sitting exactly at the start - and it is what a player
    /// reports in the instant after it begins. Treating it as "not playing" would stall the wait
    /// against a player that is fine.
    /// </summary>
    [Fact]
    public void APositionOfZeroCountsAsPlaying() =>
        Assert.True(PlayerSync.AllPlaying(new double?[] { 0.0, 0.0, 0.0 }));

    /// <summary>
    /// Nothing to wait for. Returning false here would spin the wait loop to its cap for no reason
    /// on a machine with no working players.
    /// </summary>
    [Fact]
    public void AnEmptySetIsReady() =>
        Assert.True(PlayerSync.AllPlaying(Array.Empty<double?>()));

    /// <summary>
    /// AFTER A RESUME, a player that is running has moved off zero. One still reporting 0.00 - or
    /// reporting nothing - did not take the resume, and is the frozen-display case this whole
    /// design has to earn its way past. It gets one more attempt.
    /// </summary>
    [Fact]
    public void NamesThePlayersThatDidNotResume() =>
        Assert.Equal(new[] { 1, 2 },
            PlayerSync.StalledIndexes(new double?[] { 0.12, 0.0, null }));

    [Fact]
    public void NamesNobodyWhenEveryPlayerIsRunning() =>
        Assert.Empty(PlayerSync.StalledIndexes(new double?[] { 0.12, 0.08, 0.20 }));

    /// <summary>
    /// The read timeout must stay well under the wait cap. That relationship broke on 2026-09-21 -
    /// a 400 ms read timeout under a 300 ms cap meant ONE unanswered query consumed the entire
    /// budget, every sync timed out, and the players stopped being aligned at all. The cap must fit
    /// several rounds of queries plus the pause/seek/resume burst.
    /// </summary>
    [Fact]
    public void TheWaitCapFitsSeveralRoundsOfQueries() =>
        Assert.True(PlayerSync.Timeout > TimeSpan.FromMilliseconds(MpvIpc.ReadTimeoutMs * 3));
}
