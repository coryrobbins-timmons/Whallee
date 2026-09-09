using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class ImportSummaryTests
{
    private static ImportSummaryText Describe(
        int askedSeconds, int startSeconds, int actualSeconds, string strategy = "C") =>
        ImportSummary.Describe(
            "Motorcyle Sunset",
            TimeSpan.FromSeconds(askedSeconds),
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(actualSeconds),
            strategy);

    [Fact]
    public void HeadlineNamesTheClipAndItsLength()
    {
        Assert.Equal("Imported Motorcyle Sunset — 54 seconds.", Describe(54, 0, 54).Headline);
    }

    /// <summary>
    /// Nothing differed from the request, so there is nothing to explain. A caveat here would be
    /// noise on the common path.
    /// </summary>
    [Fact]
    public void NoDetailWhenTheImportMatchedTheRequest()
    {
        Assert.Null(Describe(54, 0, 54).Detail);
    }

    [Fact]
    public void ExplainsAShortenedClip()
    {
        var detail = Describe(askedSeconds: 300, startSeconds: 0, actualSeconds: 54).Detail;
        Assert.Equal("This network capped it at 54 seconds of the 5:00 you asked for.", detail);
    }

    /// <summary>
    /// The whole reason this class exists: with the dialog predicting nothing in advance, this is
    /// the only place a user learns their start time was dropped.
    /// </summary>
    [Fact]
    public void ExplainsAnIgnoredStartTime()
    {
        var detail = Describe(askedSeconds: 54, startSeconds: 90, actualSeconds: 54).Detail;
        Assert.NotNull(detail);
        Assert.Contains("refused to start at 1:30", detail);
    }

    [Fact]
    public void CombinesBothCaveatsIntoOneSentence()
    {
        var detail = Describe(askedSeconds: 300, startSeconds: 90, actualSeconds: 54).Detail;
        Assert.NotNull(detail);
        Assert.Contains("capped it at 54 seconds", detail);
        Assert.Contains("refused to start at 1:30", detail);
        Assert.Equal(1, detail!.Count(c => c == '.'));
    }

    /// <summary>
    /// Only strategy C fetches a prefix and therefore only C can silently drop a start time.
    /// </summary>
    [Fact]
    public void DoesNotClaimAStartWasIgnoredOnOtherStrategies()
    {
        var detail = Describe(askedSeconds: 54, startSeconds: 90, actualSeconds: 54, strategy: "A").Detail;
        Assert.Null(detail);
    }

    [Theory]
    [InlineData(1, "1 second")]
    [InlineData(59, "59 seconds")]
    [InlineData(60, "1:00")]
    [InlineData(305, "5:05")]
    public void FormatsDurationsReadably(int seconds, string expected)
    {
        Assert.Contains(expected, Describe(seconds, 0, seconds).Headline);
    }
}
