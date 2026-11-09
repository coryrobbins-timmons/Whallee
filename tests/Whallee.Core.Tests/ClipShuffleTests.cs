using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class ClipShuffleTests
{
    private static readonly DateOnly Today = new(2026, 11, 6);

    private static WhalleeConfig ConfigWith(params string?[] pins) => new()
    {
        Displays = pins.Select((p, i) => new DisplayAssignment
        {
            DeviceName = $@"\\.\DISPLAY{i + 1}",
            Mode = p is null ? DisplayMode.Shuffle : DisplayMode.Pinned,
            PinnedPath = p
        }).ToList()
    };

    [Fact]
    public void DailyIsDueOnlyWhenOnAndNotYetShuffledToday()
    {
        Assert.False(ClipShuffle.IsDailyDue(false, null, Today));
        Assert.True(ClipShuffle.IsDailyDue(true, null, Today));
        Assert.True(ClipShuffle.IsDailyDue(true, Today.AddDays(-1), Today));
        Assert.False(ClipShuffle.IsDailyDue(true, Today, Today));
    }

    /// <summary>A clock set backwards must not trigger a shuffle on every start.</summary>
    [Fact]
    public void DailyIsNotDueWhenTheLastShuffleIsInTheFuture()
    {
        Assert.False(ClipShuffle.IsDailyDue(true, Today.AddDays(3), Today));
    }

    [Fact]
    public void PinsEveryDisplayAndRecordsToday()
    {
        var config = ConfigWith(null, "a.mp4", null);

        Assert.True(ClipShuffle.Apply(config, new[] { "a.mp4", "b.mp4", "c.mp4", "d.mp4" },
                                      new Random(1), Today));

        Assert.All(config.Displays, d =>
        {
            Assert.Equal(DisplayMode.Pinned, d.Mode);
            Assert.NotNull(d.PinnedPath);
        });
        Assert.Equal(Today, config.LastShuffleDate);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(42)]
    public void GivesEachDisplayADifferentClipAndAvoidsTheCurrentOne(int seed)
    {
        var config = ConfigWith("a.mp4", "b.mp4", "c.mp4");

        ClipShuffle.Apply(config, new[] { "a.mp4", "b.mp4", "c.mp4", "d.mp4", "e.mp4", "f.mp4" },
                          new Random(seed), Today);

        var pins = config.Displays.Select(d => d.PinnedPath).ToList();
        Assert.Equal(3, pins.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.NotEqual("a.mp4", pins[0]);
        Assert.NotEqual("b.mp4", pins[1]);
        Assert.NotEqual("c.mp4", pins[2]);
    }

    [Fact]
    public void StoresTheFileNameNotTheFullPath()
    {
        var config = ConfigWith((string?)null);

        ClipShuffle.Apply(config, new[] { @"C:\Library\forest.mp4" }, new Random(1), Today);

        Assert.Equal("forest.mp4", config.Displays[0].PinnedPath);
    }

    [Fact]
    public void AOneClipLibraryStillPinsEveryDisplay()
    {
        var config = ConfigWith("only.mp4", null);

        Assert.True(ClipShuffle.Apply(config, new[] { "only.mp4" }, new Random(1), Today));

        Assert.All(config.Displays, d => Assert.Equal("only.mp4", d.PinnedPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void ADeviceNameShufflesOnlyThatDisplayAndAvoidsTheOthersClips(int seed)
    {
        var config = ConfigWith("a.mp4", "b.mp4", "c.mp4");

        Assert.True(ClipShuffle.Apply(config, new[] { "a.mp4", "b.mp4", "c.mp4", "d.mp4" },
                                      new Random(seed), Today, @"\\.\DISPLAY2"));

        Assert.Equal("a.mp4", config.Displays[0].PinnedPath);
        Assert.Equal("d.mp4", config.Displays[1].PinnedPath);   // the only clip on no screen
        Assert.Equal("c.mp4", config.Displays[2].PinnedPath);
        Assert.Null(config.LastShuffleDate);                    // not the daily shuffle
    }

    [Fact]
    public void AnUnknownDeviceNameChangesNothing()
    {
        var config = ConfigWith("a.mp4");

        Assert.False(ClipShuffle.Apply(config, new[] { "a.mp4", "b.mp4" }, new Random(1), Today,
                                       @"\\.\DISPLAY9"));

        Assert.Equal("a.mp4", config.Displays[0].PinnedPath);
    }

    [Fact]
    public void AnEmptyLibraryChangesNothing()
    {
        var config = ConfigWith("a.mp4");

        Assert.False(ClipShuffle.Apply(config, Array.Empty<string>(), new Random(1), Today));

        Assert.Equal("a.mp4", config.Displays[0].PinnedPath);
        Assert.Null(config.LastShuffleDate);
    }
}
