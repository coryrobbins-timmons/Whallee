using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class DownloadProgressTests
{
    /// <summary>
    /// The case this exists for. Strategy A only started succeeding once yt-dlp was updated on
    /// 2026-09-11, and a 5-minute 1440p section then took minutes during which the status card
    /// said "Trying a direct section download…" and nothing else. yt-dlp reports progress the whole
    /// time; Whallee was discarding it.
    /// </summary>
    [Fact]
    public void DescribesAPercentAndSpeed()
    {
        var text = DownloadProgress.Describe($"{DownloadProgress.Marker}|  42.0%|   3.10MiB/s");

        Assert.Equal("Downloading… 42.0% at 3.10MiB/s", text);
    }

    /// <summary>
    /// yt-dlp pads its pre-formatted fields to fixed widths. Passing that padding straight through
    /// puts a ragged gap in the middle of the sentence.
    /// </summary>
    [Fact]
    public void TrimsYtDlpsColumnPadding()
    {
        var text = DownloadProgress.Describe($"{DownloadProgress.Marker}|   7.3%|  15.00KiB/s");

        Assert.Equal("Downloading… 7.3% at 15.00KiB/s", text);
    }

    /// <summary>
    /// Speed is unknown on the first tick and whenever a transfer stalls. A percentage alone is
    /// still worth showing - the point is that the app is visibly doing something.
    /// </summary>
    [Theory]
    [InlineData("Unknown B/s")]
    [InlineData("     N/A")]
    [InlineData("")]
    public void FallsBackToPercentAloneWhenSpeedIsUnknown(string speed)
    {
        var text = DownloadProgress.Describe($"{DownloadProgress.Marker}|  42.0%|{speed}");

        Assert.Equal("Downloading… 42.0%", text);
    }

    /// <summary>
    /// Everything yt-dlp writes that is NOT a progress line must be ignored, or ordinary chatter
    /// would overwrite the status card with noise.
    /// </summary>
    [Theory]
    [InlineData("[youtube] jIxlaOy9IE8: Downloading webpage")]
    [InlineData("[info] Downloading 1 format(s): 400+140")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("WHALLEE-PROGRESS-but-not-really")]
    public void IgnoresAnythingThatIsNotAProgressLine(string? line)
    {
        Assert.Null(DownloadProgress.Describe(line));
    }

    /// <summary>
    /// A malformed line must be dropped rather than crashing the import or printing a fragment:
    /// this parses output from an external program whose format is not ours to guarantee.
    /// </summary>
    [Theory]
    [InlineData("|")]
    [InlineData("|  42.0%")]
    [InlineData("||")]
    public void DropsMalformedProgressLines(string tail)
    {
        Assert.Null(DownloadProgress.Describe(DownloadProgress.Marker + tail));
    }

    /// <summary>
    /// The template must actually request the two fields the parser reads, in the order it reads
    /// them. These drift apart silently otherwise - the parser keeps compiling and the card just
    /// stops updating.
    /// </summary>
    [Fact]
    public void TheTemplateMatchesWhatTheParserReads()
    {
        var template = DownloadProgress.Template;

        Assert.StartsWith("download:" + DownloadProgress.Marker, template);
        Assert.Contains("_percent_str", template);
        Assert.Contains("_speed_str", template);
        Assert.True(
            template.IndexOf("_percent_str", StringComparison.Ordinal)
                < template.IndexOf("_speed_str", StringComparison.Ordinal),
            "percent must come before speed, which is the order Describe reads them in");
    }
}
