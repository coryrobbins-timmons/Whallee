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
    [InlineData(599, false)]
    [InlineData(600, false)]
    [InlineData(601, true)]
    public void RequiresSection_AboveTenMinutes(int seconds, bool expected)
    {
        Assert.Equal(expected, ImportPlanner.RequiresSection(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void PredictedObtainable_UsesTheMeasuredWindmillsNumbers()
    {
        // Spec's measured case: clen 5,330,721,038 bytes over 43,200 s at ~1 Mbps gave ~45-50 s
        // from a 6 MiB ceiling. This is the number the dialog shows BEFORE downloading.
        var predicted = ImportPlanner.PredictedObtainable(
            5_330_721_038, TimeSpan.FromSeconds(43_200), ImportPlanner.ByteCeiling);

        Assert.InRange(predicted.TotalSeconds, 45, 52);
    }

    [Fact]
    public void PredictedObtainable_ShrinksAsBitrateRises()
    {
        // Spec: ~5 Mbps typical 1080p yields only ~10 s from the same ceiling.
        var predicted = ImportPlanner.PredictedObtainable(
            625_000_000, TimeSpan.FromSeconds(1_000), ImportPlanner.ByteCeiling);

        Assert.InRange(predicted.TotalSeconds, 8, 12);
    }

    [Fact]
    public void PredictedObtainable_IsZeroForNonsenseInput()
    {
        Assert.Equal(TimeSpan.Zero,
            ImportPlanner.PredictedObtainable(0, TimeSpan.FromSeconds(10), ImportPlanner.ByteCeiling));
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
