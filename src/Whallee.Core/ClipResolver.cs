using Whallee.Core.Configuration;

namespace Whallee.Core;

public sealed class ClipResolver
{
    public static readonly string[] SupportedExtensions = { ".mp4", ".mkv", ".webm", ".mov" };

    private readonly Func<string, IReadOnlyList<string>> _listFiles;
    private readonly Random _random;

    public ClipResolver(Func<string, IReadOnlyList<string>> listFiles, Random random)
    {
        _listFiles = listFiles;
        _random = random;
    }

    /// <summary>Full path of the clip this display should play, or null if the library is empty.</summary>
    /// <exception cref="ArgumentException">
    /// The assignment pins an absolute path that lies outside <paramref name="libraryFolder"/>.
    /// </exception>
    public string? Resolve(DisplayAssignment assignment, string libraryFolder)
    {
        var candidates = _listFiles(libraryFolder);
        if (candidates.Count == 0)
            return null;

        if (assignment.Mode == DisplayMode.Pinned && !string.IsNullOrWhiteSpace(assignment.PinnedPath))
        {
            var wanted = ResolvePinnedPath(assignment.PinnedPath, libraryFolder);
            var match = candidates.FirstOrDefault(
                c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
            // Pinned clip is gone (deleted, renamed, or not yet synced) — fall through to a random
            // pick rather than leaving this display blank.
        }

        return candidates[_random.Next(candidates.Count)];
    }

    /// <summary>
    /// Turns a <see cref="DisplayAssignment.PinnedPath"/> into the full path to compare against the
    /// library listing. PinnedPath is a name relative to the library folder; a rooted path is
    /// accepted only when it points inside that folder.
    /// </summary>
    /// <remarks>
    /// Path.Combine ignores its first argument entirely when the second is rooted, so an absolute
    /// path outside the library used to pass straight through here, match no candidate, and fall
    /// back to a random clip — leaving the user with a wallpaper that ignores their pin and no way
    /// to tell that from "pinning is broken". Reject it with a message that says what to do instead.
    /// The caller (WallpaperEngine's per-display try) turns this into one error line for that
    /// display, so a bad pin never takes down the other monitors.
    /// </remarks>
    private static string ResolvePinnedPath(string pinnedPath, string libraryFolder)
    {
        if (Path.IsPathRooted(pinnedPath))
        {
            var full = Path.GetFullPath(pinnedPath);
            var prefix = Path.GetFullPath(libraryFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"pinned clip '{pinnedPath}' is an absolute path outside the library folder " +
                    $"'{libraryFolder}'. PinnedPath must name a file inside the library.",
                    nameof(pinnedPath));
        }

        return Path.Combine(libraryFolder, pinnedPath);
    }

    /// <summary>Default listing strategy: supported video files directly inside the folder.</summary>
    public static IReadOnlyList<string> ListLibraryFiles(string folder)
    {
        if (!Directory.Exists(folder))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(folder)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
