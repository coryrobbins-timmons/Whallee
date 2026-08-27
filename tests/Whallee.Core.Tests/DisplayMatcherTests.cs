using Whallee.Core;
using Whallee.Core.Configuration;
using Whallee.Core.Displays;

namespace Whallee.Core.Tests;

/// <summary>
/// Covers the "deviceId first, then deviceName" Global Constraint. Pure logic, no Win32 — which is
/// why it was extracted out of WallpaperEngine in the first place.
/// </summary>
public class DisplayMatcherTests
{
    private static DisplayInfo Display(string deviceId, string deviceName) =>
        new(deviceId, deviceName, X: 0, Y: 0, Width: 1920, Height: 1080, IsPrimary: false);

    [Fact]
    public void DeviceIdMatch_WinsOverDeviceNameMatch()
    {
        var byName = new DisplayAssignment
        {
            DeviceId = @"\\?\DISPLAY#OLD#1",
            DeviceName = @"\\.\DISPLAY1",
            PinnedPath = "wrong.mp4"
        };
        var byId = new DisplayAssignment
        {
            DeviceId = @"\\?\DISPLAY#TARGET#2",
            DeviceName = @"\\.\DISPLAY9",
            PinnedPath = "right.mp4"
        };
        var saved = new List<DisplayAssignment> { byName, byId };

        // The observed display's *name* matches the first entry and its *id* matches the second.
        // The panel is what the user pinned a clip to, so the id has to win.
        var (assignment, created) = DisplayMatcher.Resolve(
            saved, Display(@"\\?\DISPLAY#TARGET#2", @"\\.\DISPLAY1"));

        Assert.Same(byId, assignment);
        Assert.False(created);
    }

    [Fact]
    public void DeviceNameMatch_IsUsedWhenNoDeviceIdMatches()
    {
        // An assignment saved before a device id was ever recorded for it.
        var legacy = new DisplayAssignment { DeviceId = "", DeviceName = @"\\.\DISPLAY5" };
        var saved = new List<DisplayAssignment> { legacy };

        var (assignment, created) = DisplayMatcher.Resolve(
            saved, Display(@"\\?\DISPLAY#BNQ78E7#5", @"\\.\DISPLAY5"));

        Assert.Same(legacy, assignment);
        Assert.False(created);
    }

    [Fact]
    public void UnknownDisplay_GetsAShuffleDefaultAndIsNotAddedByTheMatcher()
    {
        var saved = new List<DisplayAssignment>
        {
            new() { DeviceId = @"\\?\DISPLAY#OTHER#1", DeviceName = @"\\.\DISPLAY1" }
        };

        var (assignment, created) = DisplayMatcher.Resolve(
            saved, Display(@"\\?\DISPLAY#NEW#7", @"\\.\DISPLAY7"));

        Assert.True(created);
        Assert.Equal(DisplayMode.Shuffle, assignment.Mode);
        Assert.Equal(@"\\?\DISPLAY#NEW#7", assignment.DeviceId);
        Assert.Equal(@"\\.\DISPLAY7", assignment.DeviceName);
        Assert.Null(assignment.PinnedPath);

        // Persisting is the caller's side effect, not the matcher's: the matcher stays pure so it
        // can be reasoned about (and tested) without a config to mutate.
        Assert.Single(saved);
    }

    [Fact]
    public void SavedButAbsentDisplay_IsNeverRemoved()
    {
        var absent = new DisplayAssignment
        {
            DeviceId = @"\\?\DISPLAY#UNPLUGGED#3",
            DeviceName = @"\\.\DISPLAY3",
            Mode = DisplayMode.Pinned,
            PinnedPath = "keep-me.mp4"
        };
        var present = new DisplayAssignment
        {
            DeviceId = @"\\?\DISPLAY#HERE#6",
            DeviceName = @"\\.\DISPLAY6"
        };
        var saved = new List<DisplayAssignment> { absent, present };

        // Resolving the display that IS attached must leave the unplugged monitor's pin alone, so
        // the assignment comes back when the monitor does.
        DisplayMatcher.Resolve(saved, Display(@"\\?\DISPLAY#HERE#6", @"\\.\DISPLAY6"));

        Assert.Equal(2, saved.Count);
        Assert.Contains(absent, saved);
        Assert.Equal("keep-me.mp4", absent.PinnedPath);
    }
}
