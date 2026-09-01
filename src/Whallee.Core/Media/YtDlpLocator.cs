namespace Whallee.Core.Media;

/// <summary>
/// Finds yt-dlp.exe: config first, then PATH, then the winget package tree.
/// </summary>
/// <remarks>
/// Same reasoning as <see cref="FfmpegLocator"/> - the winget path is version-stamped and moves on
/// every update, so hardcoding guarantees a silent break. One structural difference: yt-dlp.exe
/// lives DIRECTLY in its package directory, where ffmpeg is under a further bin\ subdirectory.
/// </remarks>
public static class YtDlpLocator
{
    private const string ExecutableName = "yt-dlp.exe";

    public static string DefaultWingetPackagesRoot => FfmpegLocator.DefaultWingetPackagesRoot;

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

        return listDirectories(wingetPackagesRoot)
            .Where(d => Path.GetFileName(d).StartsWith("yt-dlp", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, ExecutableName))
            .FirstOrDefault(fileExists);
    }

    /// <summary>Production entry point: real filesystem, real PATH, one-level winget search.</summary>
    public static string? Locate(string? configuredPath) => Locate(
        configuredPath,
        File.Exists,
        root => Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).ToList()
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
}
