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

    private static ImportSummaryText DescribeWithStaleTool(
        int askedSeconds, int startSeconds, int actualSeconds, int staleDays, string strategy = "C") =>
        ImportSummary.Describe(
            "Motorcyle Sunset",
            TimeSpan.FromSeconds(askedSeconds),
            TimeSpan.FromSeconds(startSeconds),
            TimeSpan.FromSeconds(actualSeconds),
            strategy,
            staleDays);

    [Fact]
    public void HeadlineNamesTheClipAndItsLength()
    {
        Assert.Equal("Imported Motorcyle Sunset — 54 seconds long.", Describe(54, 0, 54).Headline);
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
        Assert.Equal("Only 54 seconds of the 5:00 you asked for could be fetched.", detail);
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
        Assert.Contains("rather than 1:30", detail);
    }

    [Fact]
    public void CombinesBothCaveatsIntoOneSentence()
    {
        var detail = Describe(askedSeconds: 300, startSeconds: 90, actualSeconds: 54).Detail;
        Assert.NotNull(detail);
        Assert.Contains("Only 54 seconds", detail);
        Assert.Contains("rather than 1:30", detail);
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

    /// <summary>
    /// THE SENTENCE THIS WHOLE FEATURE EXISTS TO PRODUCE. It is the conclusion nobody reached for
    /// two months while a stale yt-dlp was blamed on the network.
    /// </summary>
    [Fact]
    public void NamesAStaleToolAsTheLikelyCauseOfAShortClip()
    {
        var detail = DescribeWithStaleTool(300, 0, 54, staleDays: 90).Detail;

        Assert.NotNull(detail);
        Assert.Contains("Only 54 seconds", detail);
        Assert.Contains("3 months old", detail);
        Assert.Contains("most likely cause", detail);
    }

    /// <summary>
    /// A stale tool that nonetheless delivered what was asked for has caused no harm. Saying
    /// otherwise on a successful import is how a warning becomes noise and stops being read.
    /// </summary>
    [Fact]
    public void SaysNothingAboutTheToolWhenTheImportWasNotShort()
    {
        Assert.Null(DescribeWithStaleTool(54, 0, 54, staleDays: 90).Detail);
    }

    /// <summary>
    /// An ignored start time is a strategy-C fact, not a yt-dlp-age fact. Attaching the cause
    /// sentence here would blame the wrong thing.
    /// </summary>
    [Fact]
    public void DoesNotBlameTheToolForAnIgnoredStartTimeAlone()
    {
        var detail = DescribeWithStaleTool(54, 90, 54, staleDays: 90).Detail;

        Assert.NotNull(detail);
        Assert.DoesNotContain("yt-dlp", detail);
    }

    [Fact]
    public void LeavesTheShortClipCaveatAloneWhenTheToolIsCurrent()
    {
        var detail = Describe(askedSeconds: 300, startSeconds: 0, actualSeconds: 54).Detail;

        Assert.Equal("Only 54 seconds of the 5:00 you asked for could be fetched.", detail);
    }

    /// <summary>
    /// Two facts, two sentences - unlike the two caveats, which share one. The cause is about the
    /// owner's machine, not about their request, and running them together reads as one muddle.
    /// </summary>
    [Fact]
    public void PutsTheCauseInItsOwnSentence()
    {
        var detail = DescribeWithStaleTool(300, 90, 54, staleDays: 90).Detail;

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Count(c => c == '.'));
    }
}
