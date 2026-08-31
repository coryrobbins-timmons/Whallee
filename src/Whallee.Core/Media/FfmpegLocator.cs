namespace Whallee.Core.Media;

/// <summary>
/// Finds ffmpeg.exe: config first, then PATH, then the winget package tree.
/// </summary>
/// <remarks>
/// WHY THIS IS NOT A HARDCODED PATH. The only ffmpeg on this machine lives at
/// ...\WinGet\Packages\yt-dlp.FFmpeg_...\ffmpeg-N-125875-g5d4d3bdc61-win64-gpl\bin\ffmpeg.exe.
/// That ffmpeg-N-&lt;build&gt; segment changes on every update, and the WinGet\Links shim directory,
/// while it exists, is EMPTY - so there is nothing stable to point at. Hardcoding guarantees a
/// silent break at the next update.
///
/// The three sources are ordered by how much they can be trusted:
///   1. CONFIG - the user said so, and repointing it is the documented escape hatch.
///   2. PATH - what the OS itself would resolve, and winget puts the version-stamped bin directory
///      there and rewrites it on update. Also the only source that finds an ffmpeg installed by
///      something other than winget (scoop, chocolatey, a manual unzip).
///   3. THE WINGET GLOB - the backstop for the case PATH cannot cover: the app is auto-started by
///      Explorer, Explorer has been running since before a winget update, so the process inherits
///      a stale PATH pointing at a build directory that has since been deleted.
/// Every filesystem touch is a delegate, so the search is pure logic and testable.
/// </remarks>
public static class FfmpegLocator
{
    private const string ExecutableName = "ffmpeg.exe";

    public static string DefaultWingetPackagesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WinGet", "Packages");

    public static string? Locate(
        string? configuredPath,
        Func<string, bool> fileExists,
        Func<string, IReadOnlyList<string>> listDirectories,
        string wingetPackagesRoot,
        Func<IReadOnlyList<string>>? pathDirectories = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && fileExists(configuredPath))
            return configuredPath;

        if (pathDirectories is not null)
        {
            foreach (var directory in pathDirectories())
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                var candidate = Path.Combine(directory, ExecutableName);
                if (fileExists(candidate)) return candidate;
            }
        }

        // Highest-sorting build directory wins: the build id increases over time, so ordinal
        // descending is a good enough "newest" without parsing a version string we do not control.
        return listDirectories(wingetPackagesRoot)
            .Where(d => Path.GetFileName(d).StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, "bin", ExecutableName))
            .FirstOrDefault(fileExists);
    }

    /// <summary>Production entry point: real filesystem, real PATH, two-level winget search.</summary>
    public static string? Locate(string? configuredPath) => Locate(
        configuredPath,
        File.Exists,
        root => Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).SelectMany(SafeSubdirectories).ToList()
            : Array.Empty<string>(),
        DefaultWingetPackagesRoot,
        PathDirectories);

    private static IReadOnlyList<string> PathDirectories()
    {
        try
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries |
                                           StringSplitOptions.TrimEntries)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeSubdirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
