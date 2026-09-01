using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class ImportPlannerTests
{
    [Theory]
    [InlineData("Rainy Window", "rainy-window.mp4")]
    [InlineData("Lo-fi | 10 HOURS — study", "lo-fi-10-hours-study.mp4")]
    [InlineData("  spaced   out  ", "spaced-out.mp4")]
    [InlineData("a/b\\c:d*e?f\"g<h>i", "a-b-c-d-e-f-g-h-i.mp4")]
    public void SlugFor_ProducesASafeFilename(string title, string expected)
    {
        Assert.Equal(expected, ImportPlanner.SlugFor(title));
    }

    [Fact]
    public void SlugFor_FallsBackWhenTheTitleHasNothingUsable()
    {
        // A title of pure punctuation or CJK-stripped text must not produce ".mp4".
        Assert.Equal("import.mp4", ImportPlanner.SlugFor("***"));
    }

    [Fact]
    public void SlugFor_TruncatesLongTitles()
    {
        var slug = ImportPlanner.SlugFor(new string('a', 300));

        Assert.True(slug.Length <= 64, $"slug was {slug.Length} chars");
        Assert.EndsWith(".mp4", slug);
    }

    [Theory]
    [InlineData("con", "import-con.mp4")]
    [InlineData("PRN", "import-prn.mp4")]
    [InlineData("Aux", "import-aux.mp4")]
    [InlineData("nul", "import-nul.mp4")]
    [InlineData("com1", "import-com1.mp4")]
    [InlineData("LPT9", "import-lpt9.mp4")]
    [InlineData("console", "console.mp4")]
    [InlineData("communication", "communication.mp4")]
    public void SlugFor_GuardsWindowsReservedNames(string title, string expected)
    {
        Assert.Equal(expected, ImportPlanner.SlugFor(title));
    }

    [Theory]
    [InlineData(599, false)]
    [InlineData(600, false)]
    [InlineData(601, true)]
    public void RequiresSection_AboveTenMinutes(int seconds, bool expected)
    {
        Assert.Equal(expected, ImportPlanner.RequiresSection(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void PredictedObtainable_PassesThroughARequestUnderTheCeiling()
    {
        var predicted = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3600));

        Assert.Equal(TimeSpan.FromSeconds(30), predicted);
    }

    [Fact]
    public void PredictedObtainable_CapsARequestOverTheCeiling()
    {
        var predicted = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(3600));

        Assert.Equal(ImportPlanner.ContentCeiling, predicted);
    }

    [Fact]
    public void PredictedObtainable_IsIndependentOfBitrateAndFileSize()
    {
        // This is the finding itself: measurements at ~1.0, ~3.8 and ~9.0 Mbps (spanning >10x in
        // bytes served: 6, 26, 64 MiB) all converged on ~60 s of content. The new signature
        // doesn't even accept a byte count or content length - only durations - so it is
        // structurally impossible to reintroduce bitrate arithmetic without changing the
        // signature back. Pinning that: requesting the same 300 s against videos of very
        // different total length (all longer than the ceiling) must give the identical answer,
        // where the old byte-ceiling model would have given three different ones depending on
        // each video's bitrate.
        var fromA100sVideo = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(100));
        var fromA1HourVideo = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(3_600));
        var fromA12HourVideo = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(43_200));

        Assert.Equal(ImportPlanner.ContentCeiling, fromA1HourVideo);
        Assert.Equal(fromA1HourVideo, fromA12HourVideo);
        Assert.True(fromA100sVideo <= fromA1HourVideo);   // shorter than ceiling still caps at its own length
    }

    [Fact]
    public void PredictedObtainable_CapsAtAShorterVideosOwnDuration()
    {
        var predicted = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(20), predicted);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(30, 0)]
    [InlineData(30, -10)]
    public void PredictedObtainable_IsZeroForNonsenseInput(int requestedSeconds, int durationSeconds)
    {
        Assert.Equal(TimeSpan.Zero, ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(requestedSeconds), TimeSpan.FromSeconds(durationSeconds)));
    }

    [Fact]
    public void BytesForSeconds_IsProportionalToDuration()
    {
        // 600 s of a 10,000,000-byte, 6,000 s file at the default 25% margin: (600/6000)*1.25 *
        // 10,000,000 = 1,250,000 bytes, verifiable by hand.
        var bytes = ImportPlanner.BytesForSeconds(
            TimeSpan.FromSeconds(600), 10_000_000, TimeSpan.FromSeconds(6_000));

        Assert.Equal(1_250_000, bytes);
    }

    [Fact]
    public void BytesForSeconds_MarginWidensTheRequest()
    {
        var noMargin = ImportPlanner.BytesForSeconds(
            TimeSpan.FromSeconds(600), 10_000_000, TimeSpan.FromSeconds(6_000), margin: 0);
        var withMargin = ImportPlanner.BytesForSeconds(
            TimeSpan.FromSeconds(600), 10_000_000, TimeSpan.FromSeconds(6_000), margin: 0.25);

        Assert.True(withMargin > noMargin, "a positive margin must ask for more, not less");
    }

    [Fact]
    public void BytesForSeconds_ClampsAtContentLength()
    {
        // Wanting the whole (short) file plus a 25% margin must not ask for more bytes than exist.
        var bytes = ImportPlanner.BytesForSeconds(
            TimeSpan.FromSeconds(59), 10_000_000, TimeSpan.FromSeconds(60));

        Assert.Equal(10_000_000, bytes);
    }

    [Theory]
    [InlineData(0, 10_000_000, 100)]
    [InlineData(-5, 10_000_000, 100)]
    [InlineData(30, 0, 100)]
    [InlineData(30, -1, 100)]
    [InlineData(30, 10_000_000, 0)]
    [InlineData(30, 10_000_000, -1)]
    public void BytesForSeconds_IsZeroForNonsenseInput(int seconds, long clen, int durationSeconds)
    {
        Assert.Equal(0, ImportPlanner.BytesForSeconds(
            TimeSpan.FromSeconds(seconds), clen, TimeSpan.FromSeconds(durationSeconds)));
    }

    [Fact]
    public void TrimTargetFor_AsksForLessThanArrived()
    {
        // THE moov ATOM RULE. Asking for more than arrived makes ffmpeg hit EOF and exit without
        // finalising, producing "moov atom not found". The margin is the whole point.
        var arrived = 6L * 1024 * 1024;
        var target = ImportPlanner.TrimTargetFor(arrived, 5_330_721_038, TimeSpan.FromSeconds(43_200));
        var exact = arrived / 5_330_721_038d * 43_200;

        Assert.True(target.TotalSeconds < exact, "trim target must be under what arrived");
        Assert.True(target.TotalSeconds > exact * 0.9, "margin must not throw the clip away");
    }

    [Fact]
    public void TrimTargetFor_IsZeroWhenNothingArrived()
    {
        Assert.Equal(TimeSpan.Zero,
            ImportPlanner.TrimTargetFor(0, 1_000_000, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void ValidateSection_AcceptsASectionInsideTheVideo()
    {
        Assert.Null(ImportPlanner.ValidateSection(
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ValidateSection_RejectsALengthOfZero()
    {
        Assert.NotNull(ImportPlanner.ValidateSection(
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void ValidateSection_RejectsASectionRunningPastTheEnd()
    {
        var error = ImportPlanner.ValidateSection(
            TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));

        Assert.NotNull(error);
        Assert.Contains("30", error);   // the message must name the actual limit
    }

    [Fact]
    public void ValidateSection_RejectsANegativeStart()
    {
        Assert.NotNull(ImportPlanner.ValidateSection(
            TimeSpan.FromSeconds(-1), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30)));
    }
}
