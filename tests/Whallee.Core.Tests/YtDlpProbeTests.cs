using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class YtDlpProbeTests
{
    [Fact]
    public void ParseLines_ReadsAllFourFields()
    {
        var meta = YtDlpProbe.ParseLines(new[]
            { "Rainy Window", "754", "https://i.ytimg.com/vi/x/hq.jpg", "Creative Commons Attribution" });

        Assert.NotNull(meta);
        Assert.Equal("Rainy Window", meta!.Title);
        Assert.Equal(TimeSpan.FromSeconds(754), meta.Duration);
        Assert.Equal("https://i.ytimg.com/vi/x/hq.jpg", meta.ThumbnailUrl);
        Assert.True(meta.IsCreativeCommons);
    }

    [Fact]
    public void ParseLines_KeepsAPipeInTheTitle()
    {
        // THE REASON THIS IS LINE-BASED. The spec's pipe-delimited template would split here.
        var meta = YtDlpProbe.ParseLines(new[] { "Lo-fi | 10 hours", "36000", "https://t/", "NA" });

        Assert.Equal("Lo-fi | 10 hours", meta!.Title);
        Assert.Equal(TimeSpan.FromSeconds(36000), meta.Duration);
    }

    [Fact]
    public void ParseLines_TreatsNAAsAbsent()
    {
        // yt-dlp prints the literal string NA for a field it cannot resolve.
        var meta = YtDlpProbe.ParseLines(new[] { "Clip", "60", "NA", "NA" });

        Assert.Null(meta!.ThumbnailUrl);
        Assert.Null(meta.License);
        Assert.False(meta.IsCreativeCommons);
    }

    [Theory]
    [InlineData("Creative Commons Attribution", true)]
    [InlineData("CC BY 4.0", true)]
    [InlineData("Standard YouTube License", false)]
    [InlineData("NA", false)]
    public void IsCreativeCommons_RecognisesTheLicenceStrings(string licence, bool expected)
    {
        var meta = YtDlpProbe.ParseLines(new[] { "T", "1", "NA", licence });

        Assert.Equal(expected, meta!.IsCreativeCommons);
    }

    [Fact]
    public void ParseLines_ReturnsNullWhenTooFewLines()
    {
        Assert.Null(YtDlpProbe.ParseLines(new[] { "Only a title" }));
    }

    [Fact]
    public void ParseLines_ReturnsNullWhenDurationIsNotANumber()
    {
        // A live stream reports NA for duration, and it has no section to import.
        Assert.Null(YtDlpProbe.ParseLines(new[] { "Live", "NA", "NA", "NA" }));
    }
}
