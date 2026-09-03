using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class SettingsDraftTests
{
    private static WhalleeConfig ConfigWith(ScalingMode scaling, params DisplayAssignment[] displays) =>
        new() { Scaling = scaling, Displays = displays.ToList() };

    private static DisplayAssignment Pinned(string device, string? clip) =>
        new() { DeviceName = device, Mode = DisplayMode.Pinned, PinnedPath = clip };

    private static DisplayAssignment Shuffling(string device, string? clip = null) =>
        new() { DeviceName = device, Mode = DisplayMode.Shuffle, PinnedPath = clip };

    private static Dictionary<string, DisplayChoice> Choices(
        params (string Device, DisplayMode Mode, string? Clip)[] entries)
    {
        var map = new Dictionary<string, DisplayChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) map[e.Device] = new DisplayChoice(e.Mode, e.Clip);
        return map;
    }

    // ---- nothing to do ------------------------------------------------------

    [Fact]
    public void NoChangesIsNotDirty()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Pinned(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.False(SettingsDraft.HasChanges(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Pinned, "forest.mp4"))));
    }

    [Fact]
    public void ApplyingNothingReturnsZero()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Pinned(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(0, SettingsDraft.Apply(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Pinned, "forest.mp4"))));
    }

    // ---- scaling ------------------------------------------------------------

    [Fact]
    public void ChangingScalingIsAChange()
    {
        var config = ConfigWith(ScalingMode.FillCrop);

        Assert.True(SettingsDraft.HasChanges(config, ScalingMode.Fit, Choices()));
        Assert.Equal(1, SettingsDraft.Apply(config, ScalingMode.Fit, Choices()));
        Assert.Equal(ScalingMode.Fit, config.Scaling);
    }

    // ---- the un-pin fix, which is why this page exists -----------------------

    [Fact]
    public void PinnedToShuffleClearsThePin()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Pinned(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(1, SettingsDraft.Apply(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Shuffle, null))));

        Assert.Equal(DisplayMode.Shuffle, config.Displays[0].Mode);
        Assert.Null(config.Displays[0].PinnedPath);
    }

    [Fact]
    public void ShuffleToPinnedSetsBothFields()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Shuffling(@"\\.\DISPLAY6"));

        Assert.Equal(1, SettingsDraft.Apply(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Pinned, "ocean.mp4"))));

        Assert.Equal(DisplayMode.Pinned, config.Displays[0].Mode);
        Assert.Equal("ocean.mp4", config.Displays[0].PinnedPath);
    }

    // A Shuffle display carrying a stale PinnedPath still has that path cleared, but its mode is
    // already Shuffle and must not be touched.
    [Fact]
    public void ChoosingShuffleClearsAStalePinWithoutChangingMode()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Shuffling(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(1, SettingsDraft.Apply(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Shuffle, null))));

        Assert.Equal(DisplayMode.Shuffle, config.Displays[0].Mode);
        Assert.Null(config.Displays[0].PinnedPath);
    }

    // ---- things that must NOT count as changes ------------------------------

    [Fact]
    public void PinningToTheSameClipIsNotAChange()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Pinned(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.False(SettingsDraft.HasChanges(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Pinned, "forest.mp4"))));
    }

    // "Pin this display" when nothing is playing has no clip to pin to. Doing nothing is the
    // honest outcome - writing Mode=Pinned with a null path would produce an assignment
    // ClipResolver treats as unpinned while the UI claimed it was pinned.
    [Fact]
    public void APinnedChoiceWithNoFileNameIsIgnored()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Shuffling(@"\\.\DISPLAY6"));

        Assert.False(SettingsDraft.HasChanges(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Pinned, null))));
        Assert.Equal(0, SettingsDraft.Apply(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY6", DisplayMode.Pinned, null))));
        Assert.Equal(DisplayMode.Shuffle, config.Displays[0].Mode);
    }

    [Fact]
    public void AnUnknownDeviceNameIsIgnored()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Shuffling(@"\\.\DISPLAY6"));

        Assert.Equal(0, SettingsDraft.Apply(config, ScalingMode.FillCrop,
            Choices((@"\\.\DISPLAY99", DisplayMode.Pinned, "ocean.mp4"))));
    }

    [Fact]
    public void DeviceNameMatchingIsCaseInsensitive()
    {
        var config = ConfigWith(ScalingMode.FillCrop, Pinned(@"\\.\DISPLAY6", "forest.mp4"));

        Assert.Equal(1, SettingsDraft.Apply(config, ScalingMode.FillCrop,
            Choices((@"\\.\display6", DisplayMode.Shuffle, null))));
        Assert.Equal(DisplayMode.Shuffle, config.Displays[0].Mode);
    }

    // ---- counting -----------------------------------------------------------

    [Fact]
    public void ApplyCountsEveryChangedField()
    {
        var config = ConfigWith(ScalingMode.FillCrop,
            Pinned(@"\\.\DISPLAY6", "forest.mp4"),
            Pinned(@"\\.\DISPLAY5", "ocean.mp4"),
            Shuffling(@"\\.\DISPLAY1"));

        // scaling + two displays un-pinned = 3
        Assert.Equal(3, SettingsDraft.Apply(config, ScalingMode.Fit,
            Choices((@"\\.\DISPLAY6", DisplayMode.Shuffle, null),
                    (@"\\.\DISPLAY5", DisplayMode.Shuffle, null),
                    (@"\\.\DISPLAY1", DisplayMode.Shuffle, null))));
    }

    // ---- which clip the pin dropdown starts on ------------------------------

    // The saved pin wins over what is playing: a pinned display that fell back to a random clip
    // (its file deleted or not yet synced) is still CONFIGURED for the pin the user chose.
    [Fact]
    public void InitialPinPrefersTheSavedPinOverWhatIsPlaying() =>
        Assert.Equal("forest.mp4", SettingsDraft.InitialPin("forest.mp4", "ocean.mp4"));

    [Fact]
    public void InitialPinFallsBackToWhatIsPlaying() =>
        Assert.Equal("ocean.mp4", SettingsDraft.InitialPin(null, "ocean.mp4"));

    [Fact]
    public void InitialPinIsNullWhenNothingIsSavedOrPlaying() =>
        Assert.Null(SettingsDraft.InitialPin(null, null));

    [Fact]
    public void InitialPinTreatsBlankAsAbsent() =>
        Assert.Equal("ocean.mp4", SettingsDraft.InitialPin("   ", "ocean.mp4"));

    // ClipResolver accepts a rooted pin inside the library, so the dropdown must match on the bare
    // name or it would never select the saved entry.
    [Fact]
    public void InitialPinReducesARootedSavedPinToItsFileName() =>
        Assert.Equal("forest.mp4", SettingsDraft.InitialPin(@"C:\lib\forest.mp4", null));
}
