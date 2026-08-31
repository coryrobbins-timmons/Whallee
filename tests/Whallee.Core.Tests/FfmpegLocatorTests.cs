using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class FfmpegLocatorTests
{
    private const string Root = @"C:\winget\Packages";

    private static string? Locate(
        string? configured,
        IEnumerable<string> existingFiles,
        IEnumerable<string> directories,
        IEnumerable<string>? pathDirectories = null) =>
        FfmpegLocator.Locate(
            configured,
            path => existingFiles.Contains(path, StringComparer.OrdinalIgnoreCase),
            _ => directories.ToList(),
            Root,
            () => pathDirectories?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>());

    // ---- config wins -------------------------------------------------------

    [Fact]
    public void ConfiguredPathThatExistsIsUsedUnchanged() =>
        Assert.Equal(
            @"D:\tools\ffmpeg.exe",
            Locate(@"D:\tools\ffmpeg.exe", new[] { @"D:\tools\ffmpeg.exe" }, Array.Empty<string>()));

    [Fact]
    public void ConfiguredPathThatIsMissingFallsBackToDiscovery()
    {
        var found = Root + @"\yt-dlp.FFmpeg_x\ffmpeg-N-1-win64-gpl\bin\ffmpeg.exe";

        Assert.Equal(found, Locate(@"D:\gone\ffmpeg.exe", new[] { found },
            new[] { Root + @"\yt-dlp.FFmpeg_x\ffmpeg-N-1-win64-gpl" }));
    }

    [Fact]
    public void BlankConfiguredPathIsTreatedAsUnset() =>
        Assert.Null(Locate("   ", Array.Empty<string>(), Array.Empty<string>()));

    // ---- PATH, tried before the winget glob --------------------------------

    [Fact]
    public void PathIsSearchedWhenConfigIsUnset()
    {
        var onPath = @"C:\tools\bin\ffmpeg.exe";

        Assert.Equal(onPath, Locate(null, new[] { onPath }, Array.Empty<string>(),
            new[] { @"C:\tools\bin" }));
    }

    [Fact]
    public void PathBeatsTheWingetGlob()
    {
        // PATH is what the OS itself would resolve, and on this machine winget puts the
        // version-stamped bin directory there and rewrites it on update.
        var onPath = @"C:\tools\bin\ffmpeg.exe";
        var globbed = Root + @"\p\ffmpeg-N-1-win64-gpl\bin\ffmpeg.exe";

        Assert.Equal(onPath, Locate(null, new[] { onPath, globbed },
            new[] { Root + @"\p\ffmpeg-N-1-win64-gpl" }, new[] { @"C:\tools\bin" }));
    }

    [Fact]
    public void AStalePathEntryFallsThroughToTheGlob()
    {
        // The real scenario this guards: the app is auto-started by Explorer, which has been
        // running since before a winget update, so it inherits a PATH pointing at a build
        // directory that no longer exists.
        var globbed = Root + @"\p\ffmpeg-N-2-win64-gpl\bin\ffmpeg.exe";

        Assert.Equal(globbed, Locate(null, new[] { globbed },
            new[] { Root + @"\p\ffmpeg-N-2-win64-gpl" },
            new[] { @"C:\gone\ffmpeg-N-1-win64-gpl\bin" }));
    }

    // ---- winget glob -------------------------------------------------------

    [Fact]
    public void DiscoveryPrefersTheHighestSortingBuildWhenSeveralExist()
    {
        var older = Root + @"\p\ffmpeg-N-100-win64-gpl\bin\ffmpeg.exe";
        var newer = Root + @"\p\ffmpeg-N-200-win64-gpl\bin\ffmpeg.exe";

        Assert.Equal(newer, Locate(null, new[] { older, newer },
            new[] { Root + @"\p\ffmpeg-N-100-win64-gpl", Root + @"\p\ffmpeg-N-200-win64-gpl" }));
    }

    [Fact]
    public void DiscoveryIgnoresDirectoriesThatAreNotFfmpegBuilds()
    {
        var found = Root + @"\p\ffmpeg-N-1-win64-gpl\bin\ffmpeg.exe";

        Assert.Equal(found, Locate(null, new[] { found },
            new[] { Root + @"\p\yt-dlp-2025", Root + @"\p\ffmpeg-N-1-win64-gpl" }));
    }

    [Fact]
    public void IgnoresDirectoriesThatDoNotContainTheExecutable() =>
        Assert.Null(Locate(null, Array.Empty<string>(),
            new[] { Root + @"\p\ffmpeg-N-1-win64-gpl" }));

    [Fact]
    public void ReturnsNullWhenNothingIsFound() =>
        Assert.Null(Locate(null, Array.Empty<string>(), Array.Empty<string>()));

    // ---- the real machine --------------------------------------------------

    [Fact]
    public void DefaultWingetPackagesRootPointsAtTheRealWingetLayout()
    {
        var root = FfmpegLocator.DefaultWingetPackagesRoot;

        Assert.EndsWith(Path.Combine("Microsoft", "WinGet", "Packages"), root);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), root);
    }
}
