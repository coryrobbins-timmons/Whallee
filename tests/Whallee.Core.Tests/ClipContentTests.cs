using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class ClipContentTests
{
    /// <summary>
    /// Captured verbatim from the failing import on 2026-09-10. This exact line is the answer the
    /// bitrate estimate got wrong: it asked for 36.9s of a prefix that held 33.62s.
    /// </summary>
    [Fact]
    public void ReadsTheContentLengthFromFfmpegsFinalStatsLine()
    {
        const string stderr =
            "[libdav1d @ 0000] Failed to parse temporal unit.\n" +
            "[in#0/mov,mp4,m4a,3gp,3g2,mj2 @ 0000] stream 0, offset 0x20064e: partial file\n" +
            "frame=  807 fps=0.0 q=-1.0 Lsize=N/A time=00:00:33.62 bitrate=N/A speed=1.64e+03x\n";

        Assert.Equal(
            TimeSpan.FromSeconds(33.62),
            ClipContent.ParseMeasuredLength(stderr));
    }

    /// <summary>
    /// ffmpeg rewrites its stats line in place, so a drained stderr holds every intermediate
    /// value. Only the last one is the length actually reached.
    /// </summary>
    [Fact]
    public void TakesTheLastTimeNotTheFirst()
    {
        const string stderr =
            "frame=  100 time=00:00:04.16 speed=1x\r" +
            "frame=  400 time=00:00:16.66 speed=1x\r" +
            "frame=  807 time=00:00:33.62 speed=1x\r";

        Assert.Equal(TimeSpan.FromSeconds(33.62), ClipContent.ParseMeasuredLength(stderr));
    }

    [Fact]
    public void ReadsLengthsPastAnHour()
    {
        Assert.Equal(
            new TimeSpan(0, 2, 15, 7, 500),
            ClipContent.ParseMeasuredLength("time=02:15:07.50 bitrate=N/A\n"));
    }

    /// <summary>
    /// Nothing parseable means NOT MEASURED, never zero. Zero would read as "the file is empty"
    /// and fail an import the caller could still complete from its estimate.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ffmpeg version n7.1 Copyright (c) the FFmpeg developers\n")]
    [InlineData("time=N/A bitrate=N/A\n")]
    public void UnparseableOutputMeasuresNothing(string? stderr)
    {
        Assert.Null(ClipContent.ParseMeasuredLength(stderr));
    }

    /// <summary>
    /// ffmpeg prints a huge negative placeholder before the first real timestamp is known.
    /// Accepting it would compute a negative trim length.
    /// </summary>
    [Fact]
    public void IgnoresTheNegativePlaceholder()
    {
        Assert.Null(ClipContent.ParseMeasuredLength("time=-577014:32:22.77 bitrate=N/A\n"));
    }

    /// <summary>
    /// THE BOUNDARY, MEASURED ON THE REAL FILE. Trimming to exactly the length ffmpeg reported
    /// (33.62s) still produced "moov atom not found"; 33.50s produced a clean file. Reaching the
    /// final packet is enough to trip the truncated-input path, so the target must sit strictly
    /// inside it.
    /// </summary>
    [Fact]
    public void UsableLengthStopsShortOfTheMeasuredEnd()
    {
        var usable = ClipContent.UsableLength(TimeSpan.FromSeconds(33.62));

        Assert.True(usable < TimeSpan.FromSeconds(33.62), "must not ask for the whole measurement");
        Assert.True(usable <= TimeSpan.FromSeconds(33.50),
            "the 33.50s that verified clean is the loosest defensible target");
        Assert.True(usable > TimeSpan.FromSeconds(30), "and must not throw away usable video");
    }

    /// <summary>
    /// A measurement smaller than the safety margin has nothing usable in it. Zero is the honest
    /// answer; a negative length would reach ffmpeg as an argument it rejects.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    public void UsableLengthIsNeverNegative(double measuredSeconds)
    {
        Assert.Equal(TimeSpan.Zero, ClipContent.UsableLength(TimeSpan.FromSeconds(measuredSeconds)));
    }
}
