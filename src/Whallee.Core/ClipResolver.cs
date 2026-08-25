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
    public string? Resolve(DisplayAssignment assignment, string libraryFolder)
    {
        var candidates = _listFiles(libraryFolder);
        if (candidates.Count == 0)
            return null;

        if (assignment.Mode == DisplayMode.Pinned && !string.IsNullOrWhiteSpace(assignment.PinnedPath))
        {
            var wanted = Path.Combine(libraryFolder, assignment.PinnedPath);
            var match = candidates.FirstOrDefault(
                c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
            // Pinned clip is gone (deleted, renamed, or not yet synced) — fall through to a random
            // pick rather than leaving this display blank.
        }

        return candidates[_random.Next(candidates.Count)];
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
