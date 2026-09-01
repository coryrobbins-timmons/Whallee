using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class YtDlpLocatorTests
{
    private static IReadOnlyList<string> NoDirectories(string _) => Array.Empty<string>();

    [Fact]
    public void ConfiguredPath_WinsWhenItExists()
    {
        var found = YtDlpLocator.Locate(
            @"C:\tools\yt-dlp.exe", p => p == @"C:\tools\yt-dlp.exe",
            NoDirectories, @"C:\winget");

        Assert.Equal(@"C:\tools\yt-dlp.exe", found);
    }

    [Fact]
    public void ConfiguredPath_IsSkippedWhenItNoLongerExists()
    {
        // The winget path is version-stamped, so a config value CAN go stale. Falling through
        // rather than failing is the whole reason discovery still runs when config names a path.
        var found = YtDlpLocator.Locate(
            @"C:\gone\yt-dlp.exe", p => p == @"C:\winget\yt-dlp.x\yt-dlp.exe",
            root => root == @"C:\winget" ? new[] { @"C:\winget\yt-dlp.x" } : Array.Empty<string>(),
            @"C:\winget");

        Assert.Equal(@"C:\winget\yt-dlp.x\yt-dlp.exe", found);
    }

    [Fact]
    public void WingetGlob_TakesTheHighestSortingPackageDirectory()
    {
        var found = YtDlpLocator.Locate(
            null, p => p.StartsWith(@"C:\winget\yt-dlp."),
            _ => new[] { @"C:\winget\yt-dlp.a", @"C:\winget\yt-dlp.z", @"C:\winget\ffmpeg.q" },
            @"C:\winget");

        Assert.Equal(@"C:\winget\yt-dlp.z\yt-dlp.exe", found);
    }

    [Fact]
    public void PathDirectories_AreSearchedBeforeTheGlob()
    {
        var found = YtDlpLocator.Locate(
            null, p => p == @"C:\onpath\yt-dlp.exe", _ => new[] { @"C:\winget\yt-dlp.a" },
            @"C:\winget", () => new[] { @"C:\onpath" });

        Assert.Equal(@"C:\onpath\yt-dlp.exe", found);
    }

    [Fact]
    public void ReturnsNull_WhenNothingIsFound()
    {
        Assert.Null(YtDlpLocator.Locate(null, _ => false, NoDirectories, @"C:\winget"));
    }
}
