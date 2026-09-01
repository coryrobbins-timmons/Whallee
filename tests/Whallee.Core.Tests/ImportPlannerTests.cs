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
    public void PredictedObtainable_SaturatesAtTheCeilingForAnyDurationPastIt()
    {
        // BITRATE INDEPENDENCE IS ENFORCED BY THE SIGNATURE, NOT BY THIS TEST: PredictedObtainable
        // takes no clen. Reintroducing byte arithmetic would break every call site at compile time,
        // which is a stronger guarantee than any assertion here could give. What this test pins is
        // narrower - that the min-chain saturates identically once the video outruns the ceiling.
        var fromA100sVideo = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(100));
        var fromA1HourVideo = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(3_600));
        var fromA12HourVideo = ImportPlanner.PredictedObtainable(
            TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(43_200));

        Assert.Equal(ImportPlanner.ContentCeiling, fromA100sVideo);
        Assert.Equal(ImportPlanner.ContentCeiling, fromA1HourVideo);
        Assert.Equal(ImportPlanner.ContentCeiling, fromA12HourVideo);
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
    public void BytesForSeconds_HandlesRealMultiGigabyteMagnitudes()
    {
        // 20,679,065,225 bytes / 43,200 s is the measured clen/duration of one of the videos this
        // code was built against - the same order of magnitude TrimTargetFor is already tested at.
        // Wanting 60 s at the default 25% margin: (60/43200)*1.25*20,679,065,225 = 35,901,154.90...,
        // computed by hand; asserted within a tight tolerance that would still catch a
        // precision-loss or overflow error at this scale.
        var bytes = ImportPlanner.BytesForSeconds(
            TimeSpan.FromSeconds(60), 20_679_065_225, TimeSpan.FromSeconds(43_200));

        Assert.InRange(bytes, 35_901_150, 35_901_160);
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
