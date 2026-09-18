using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class PlayerSyncTests
{
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
    /// The cap exists so a pass never stalls on unreachable pipes, and it is only useful if it is
    /// comfortably longer than a poll. A cap at or below one interval would time out before the
    /// first check.
    /// </summary>
    [Fact]
    public void TheTimeoutIsLongerThanAPollInterval()
    {
        Assert.True(PlayerSync.Timeout > PlayerSync.PollInterval);
    }
}
