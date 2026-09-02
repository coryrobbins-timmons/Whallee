using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class ClipRemovalTests
{
    private static WhalleeConfig ConfigWith(params DisplayAssignment[] displays) =>
        new() { Displays = displays.ToList() };

    private static DisplayAssignment Pinned(string device, string? clip) =>
        new() { DeviceName = device, Mode = DisplayMode.Pinned, PinnedPath = clip };

    private static DisplayAssignment Shuffling(string device, string? clip = null) =>
        new() { DeviceName = device, Mode = DisplayMode.Shuffle, PinnedPath = clip };

    // ---- counting -----------------------------------------------------------

    [Fact]
    public void CountsOnlyTheDisplaysPinnedToTheClip()
    {
        var config = ConfigWith(
            Pinned(@"\\.\DISPLAY6", "forest.mp4"),
            Pinned(@"\\.\DISPLAY5", "ocean.mp4"),
            Shuffling(@"\\.\DISPLAY1"));

        Assert.Equal(1, ClipRemoval.CountReferences(config, "forest.mp4"));
    }

    [Fact]
    public void CountsEveryDisplayWhenTheClipIsPinnedToAll()
    {
        var config = ConfigWith(
            Pinned(@"\\.\DISPLAY6", "forest.mp4"),
            Pinned(@"\\.\DISPLAY5", "forest.mp4"),
            Pinned(@"\\.\DISPLAY1", "forest.mp4"));

        Assert.Equal(3, ClipRemoval.CountReferences(config, "forest.mp4"));
    }

    [Fact]
    public void CountIsZeroWhenNothingIsPinnedToIt() =>
        Assert.Equal(0, ClipRemoval.CountReferences(
            ConfigWith(Shuffling(@"\\.\DISPLAY6"), Pinned(@"\\.\DISPLAY5", "ocean.mp4")),
            "forest.mp4"));

    [Fact]
    public void MatchingIsCaseInsensitive() =>
        Assert.Equal(1, ClipRemoval.CountReferences(
            ConfigWith(Pinned(@"\\.\DISPLAY6", "Forest.MP4")), "forest.mp4"));

    [Fact]
    public void ARootedPinnedPathInsideTheLibraryStillMatches() =>
        Assert.Equal(1, ClipRemoval.CountReferences(
            ConfigWith(Pinned(@"\\.\DISPLAY6", @"C:\lib\forest.mp4")), "forest.mp4"));

    [Fact]
    public void ANullPinnedPathIsIgnored() =>
        Assert.Equal(0, ClipRemoval.CountReferences(
            ConfigWith(Pinned(@"\\.\DISPLAY6", null)), "forest.mp4"));

    // A Shuffle display is not SHOWING the clip because of its stale PinnedPath, so counting it
    // would make the confirm dialog claim a consequence that will not happen.
    [Fact]
    public void AStaleReferenceOnAShufflingDisplayIsNotCounted() =>
        Assert.Equal(0, ClipRemoval.CountReferences(
            ConfigWith(Shuffling(@"\\.\DISPLAY6", "forest.mp4")), "forest.mp4"));

    // ---- clearing -----------------------------------------------------------

    [Fact]
    public void ClearingDropsAPinnedDisplayToShuffle()
    {
        var config = ConfigWith(Pinned(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(1, ClipRemoval.ClearAssignments(config, "forest.mp4"));
        Assert.Equal(DisplayMode.Shuffle, config.Displays[0].Mode);
        Assert.Null(config.Displays[0].PinnedPath);
    }

    // Clearing DOES remove a stale reference even though counting ignores it - a dangling path is
    // worth tidying - but it must not change a mode that was already Shuffle.
    [Fact]
    public void ClearingAStaleReferenceLeavesShuffleModeAlone()
    {
        var config = ConfigWith(Shuffling(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(1, ClipRemoval.ClearAssignments(config, "forest.mp4"));
        Assert.Equal(DisplayMode.Shuffle, config.Displays[0].Mode);
        Assert.Null(config.Displays[0].PinnedPath);
    }

    [Fact]
    public void ClearingLeavesAnotherClipsPinUntouched()
    {
        var config = ConfigWith(
            Pinned(@"\\.\DISPLAY6", "forest.mp4"),
            Pinned(@"\\.\DISPLAY5", "ocean.mp4"));

        Assert.Equal(1, ClipRemoval.ClearAssignments(config, "forest.mp4"));
        Assert.Equal(DisplayMode.Pinned, config.Displays[1].Mode);
        Assert.Equal("ocean.mp4", config.Displays[1].PinnedPath);
    }

    // ---- names for the confirm dialog ---------------------------------------

    [Fact]
    public void PinnedDisplayNamesListsOnlyThePinnedDevices()
    {
        var config = ConfigWith(
            Pinned(@"\\.\DISPLAY6", "forest.mp4"),
            Shuffling(@"\\.\DISPLAY5", "forest.mp4"),
            Pinned(@"\\.\DISPLAY1", "forest.mp4"));

        Assert.Equal(new[] { @"\\.\DISPLAY6", @"\\.\DISPLAY1" },
                     ClipRemoval.PinnedDisplayNames(config, "forest.mp4"));
    }
}
