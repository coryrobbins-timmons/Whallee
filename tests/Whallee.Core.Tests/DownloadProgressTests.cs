using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class DownloadProgressTests
{
    /// <summary>
    /// Captured verbatim from a real section download on 2026-09-11. ffmpeg rewrites its stats in
    /// place with carriage returns, so one read routinely holds several updates and only the last
    /// is current.
    /// </summary>
    private const string Chunk =
        "frame=   31 fps=0.0 q=-1.0 size=       0KiB time=00:00:01.29 bitrate=   0.3kbits/s speed=2.51x elapsed=0:00:00.51    \r"
      + "frame=   78 fps= 50 q=-1.0 size=     256KiB time=00:00:03.25 bitrate= 645.4kbits/s speed= 2.1x elapsed=0:00:01.54    \r"
      + "frame=  121 fps= 39 q=-1.0 size=     256KiB time=00:00:04.00 bitrate= 406.9kbits/s speed=2.00x elapsed=0:00:03.09    \r";

    /// <summary>
    /// THE POINT OF THE CLASS: ffmpeg never prints a percentage, because it does not know a section
    /// was requested. The caller's requested length is the denominator.
    /// </summary>
    [Fact]
    public void DerivesAPercentageFromTheRequestedLength()
    {
        var text = DownloadProgress.Describe(Chunk, TimeSpan.FromSeconds(8));

        Assert.StartsWith("Downloading… 50%", text);
    }

    /// <summary>
    /// Takes the LAST timestamp in the chunk. Reading the first would report 16% while the download
    /// is actually half done, and would go backwards on the next read.
    /// </summary>
    [Fact]
    public void UsesTheLastUpdateInTheChunkNotTheFirst()
    {
        var text = DownloadProgress.Describe(Chunk, TimeSpan.FromSeconds(8));

        Assert.DoesNotContain("16%", text);
    }

    /// <summary>
    /// 4 s done of 8 s at 2x real time leaves 4 s of work, so ~2 s - "a few seconds".
    /// </summary>
    [Fact]
    public void EstimatesHowMuchLongerFromFfmpegsSpeedMultiplier()
    {
        var text = DownloadProgress.Describe(Chunk, TimeSpan.FromSeconds(8));

        Assert.Equal("Downloading… 50% — about a few seconds left", text);
    }

    /// <summary>
    /// The case the owner actually hit: a 5-minute section. 60 s done of 300 s at 0.5x leaves 240 s
    /// of work at half speed - about 8 minutes.
    /// </summary>
    [Fact]
    public void ReportsMinutesOnALongSection()
    {
        var text = DownloadProgress.Describe(
            "frame= 1440 time=00:01:00.00 bitrate=800kbits/s speed=0.5x", TimeSpan.FromMinutes(5));

        Assert.Equal("Downloading… 20% — about 8 minutes left", text);
    }

    /// <summary>
    /// ffmpeg can overshoot the requested length on the final packet. "101%" reads as a bug.
    /// </summary>
    [Fact]
    public void NeverReportsMoreThanOneHundredPercent()
    {
        var text = DownloadProgress.Describe(
            "time=00:00:09.10 speed=2.00x", TimeSpan.FromSeconds(8));

        Assert.StartsWith("Downloading… 100%", text);
    }

    /// <summary>
    /// No speed figure on the first tick. A percentage alone still does the job this line exists
    /// for - showing the app is working rather than hung.
    /// </summary>
    [Fact]
    public void FallsBackToPercentAloneWhenSpeedIsMissing()
    {
        var text = DownloadProgress.Describe("time=00:00:04.00 bitrate=N/A", TimeSpan.FromSeconds(8));

        Assert.Equal("Downloading… 50%", text);
    }

    [Fact]
    public void FallsBackToPercentAloneWhenSpeedIsZero()
    {
        var text = DownloadProgress.Describe("time=00:00:04.00 speed=0.00x", TimeSpan.FromSeconds(8));

        Assert.Equal("Downloading… 50%", text);
    }

    /// <summary>
    /// No requested length means no denominator, but the elapsed figure is still worth showing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ShowsElapsedWhenThereIsNoLengthToMeasureAgainst(int seconds)
    {
        var text = DownloadProgress.Describe(Chunk, TimeSpan.FromSeconds(seconds));

        Assert.Equal("Downloading… 4 seconds so far", text);
    }

    /// <summary>
    /// ffmpeg's stderr carries far more than progress - stream metadata, the whole signed media
    /// URL, warnings. None of it may overwrite the status line.
    /// </summary>
    [Theory]
    [InlineData("Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'https://rr4---sn-bvv.googlevideo.com/...':")]
    [InlineData("  Metadata:")]
    [InlineData("Press [q] to stop, [?] for help")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresOutputThatCarriesNoTimestamp(string? chunk)
    {
        Assert.Null(DownloadProgress.Describe(chunk, TimeSpan.FromSeconds(8)));
    }

    /// <summary>
    /// OBSERVED, NOT IMAGINED. A real import on 2026-09-11 opened with "about 49 minutes left",
    /// then 100, then 148, on a clip that finished in seconds - ffmpeg's speed multiplier starts
    /// near zero while it is still opening the stream. A wildly wrong estimate is worse than none:
    /// it creates exactly the "this is taking forever" impression the line exists to remove.
    /// </summary>
    [Fact]
    public void WithholdsTheEstimateUntilEnoughProgressToTrustIt()
    {
        var text = DownloadProgress.Describe(
            "time=00:00:00.16 bitrate=0.3kbits/s speed=0.006x", TimeSpan.FromSeconds(20));

        Assert.Equal("Downloading… 1%", text);
    }

    /// <summary>
    /// The other half of that rule: once past the floor the estimate must actually appear, or the
    /// guard would silently suppress it for the whole download.
    /// </summary>
    [Fact]
    public void ShowsTheEstimateOncePastTheFloor()
    {
        var text = DownloadProgress.Describe(
            "time=00:00:02.00 speed=1.00x", TimeSpan.FromSeconds(20));

        Assert.Equal("Downloading… 10% — about a few seconds left", text);
    }

    /// <summary>
    /// ffmpeg prints a huge negative placeholder before the first real timestamp. Accepting it
    /// would show a nonsense percentage on the first tick.
    /// </summary>
    [Fact]
    public void IgnoresFfmpegsNegativePlaceholder()
    {
        Assert.Null(DownloadProgress.Describe(
            "time=-577014:32:22.77 bitrate=N/A speed=1.00x", TimeSpan.FromSeconds(8)));
    }
}
