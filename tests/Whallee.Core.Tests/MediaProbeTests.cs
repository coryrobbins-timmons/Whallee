using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class MediaProbeTests
{
    [Fact]
    public void ReadsWidthHeightAndDuration()
    {
        var info = MediaProbe.Parse("width=1920\nheight=1080\nduration=57.033000\n");

        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(57.033, info.Duration.TotalSeconds, 3);
    }

    // The parser reads KEYS, so it does not care which section ffprobe emits first. That is the
    // entire reason the invocation uses "-of default=noprint_wrappers=1" and not the nokey form.
    [Fact]
    public void TheOrderOfTheLinesDoesNotMatter()
    {
        var info = MediaProbe.Parse("duration=12.5\nheight=720\nwidth=1280");

        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal(12.5, info.Duration.TotalSeconds, 3);
    }

    [Fact]
    public void WindowsLineEndingsAreTolerated()
    {
        var info = MediaProbe.Parse("width=3840\r\nheight=2160\r\nduration=57.0\r\n");

        Assert.Equal(3840, info.Width);
        Assert.Equal(2160, info.Height);
        Assert.True(info.HasDuration);
    }

    [Fact]
    public void EmptyOutputGivesNone() => Assert.Equal(MediaInfo.None, MediaProbe.Parse(""));

    [Fact]
    public void NullOutputGivesNone() => Assert.Equal(MediaInfo.None, MediaProbe.Parse(null));

    [Fact]
    public void GarbageOutputGivesNone() =>
        Assert.Equal(MediaInfo.None, MediaProbe.Parse("no such thing\n???\n"));

    [Fact]
    public void AMissingDurationStillYieldsTheResolution()
    {
        var info = MediaProbe.Parse("width=1920\nheight=1080\n");

        Assert.True(info.HasResolution);
        Assert.False(info.HasDuration);
    }

    [Fact]
    public void AnUnparseableDurationIsTreatedAsUnknown() =>
        Assert.False(MediaProbe.Parse("duration=N/A\nwidth=1920\nheight=1080").HasDuration);

    // ffprobe always prints a dot. Parsing with the current culture would read "57.033" as 57033
    // on a comma-decimal machine.
    [Fact]
    public void TheDurationIsParsedInvariantly() =>
        Assert.Equal(57.033, MediaProbe.Parse("duration=57.033").Duration.TotalSeconds, 3);

    [Fact]
    public void AZeroDurationIsTreatedAsUnknown() =>
        Assert.False(MediaProbe.Parse("duration=0").HasDuration);
}
