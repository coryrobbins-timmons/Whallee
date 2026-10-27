using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class YtDlpUpdaterTests
{
    private const string WingetRoot = @"C:\Users\someone\AppData\Local\Microsoft\WinGet\Packages";

    [Fact]
    public void AWingetCopyIsUpgradedThroughWinget()
    {
        var path = WingetRoot + @"\yt-dlp.yt-dlp_Microsoft.Winget.Source_8wekyb3d8bbwe\yt-dlp.exe";

        var (fileName, arguments) = YtDlpUpdater.CommandFor(path, WingetRoot);

        Assert.Equal("winget", fileName);
        Assert.Equal("upgrade", arguments[0]);
        Assert.Contains("yt-dlp.yt-dlp", arguments);
        Assert.Contains("--exact", arguments);
        Assert.Contains("--disable-interactivity", arguments);
    }

    [Fact]
    public void AStandaloneCopyUpdatesItself()
    {
        var path = @"C:\Tools\yt-dlp.exe";

        var (fileName, arguments) = YtDlpUpdater.CommandFor(path, WingetRoot);

        Assert.Equal(path, fileName);
        Assert.Equal(new[] { "-U" }, arguments);
    }

    /// <summary>The button and the warning text must name the same command for every path.</summary>
    [Theory]
    [InlineData(WingetRoot + @"\yt-dlp.yt-dlp_x\yt-dlp.exe")]
    [InlineData(@"C:\Tools\yt-dlp.exe")]
    [InlineData(WingetRoot + @"Extra\yt-dlp.exe")]
    public void AgreesWithTheWarningText(string path)
    {
        var warned = YtDlpVersion.UpdateCommandFor(path, WingetRoot);
        var (fileName, _) = YtDlpUpdater.CommandFor(path, WingetRoot);

        Assert.Equal(warned.StartsWith("winget"), fileName == "winget");
    }

    [Fact]
    public void LastLinePrefersStderr()
    {
        Assert.Equal("ERROR: use pip to update",
            YtDlpUpdater.LastLine("Current version: 2026.08.19\n", "ERROR: use pip to update\n"));
    }

    [Fact]
    public void LastLineFallsBackToStdoutAndSkipsBlankLines()
    {
        Assert.Equal("Updated yt-dlp to 2026.10.20",
            YtDlpUpdater.LastLine("Latest version: 2026.10.20\nUpdated yt-dlp to 2026.10.20\n\n  \n", ""));
    }

    [Fact]
    public void LastLineIsNullWhenNothingWasSaid()
    {
        Assert.Null(YtDlpUpdater.LastLine(null, "  \n"));
    }
}
