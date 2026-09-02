namespace Whallee.Core.Configuration;

/// <summary>
/// Config surgery for a clip being removed from the library.
/// </summary>
/// <remarks>
/// PURE, AND DELIBERATELY SO. This is the only part of deleting a clip that can be unit tested -
/// the recycle is a shell call and the re-attach is a Win32 window operation, both of which
/// standing ruling 3 keeps out of the suite. Putting the decision logic here means the risky half
/// of Delete is the half with coverage.
///
/// PinnedPath is written by LibraryViewModel.ApplyClip as a BARE FILE NAME relative to the library
/// folder, but Path.GetFileName is applied to it here anyway: ClipResolver.ResolvePinnedPath also
/// accepts a rooted path that points inside the library, and such a value would otherwise never
/// match.
/// </remarks>
public static class ClipRemoval
{
    /// <summary>
    /// How many displays would visibly change if this clip vanished - assignments actually Pinned
    /// to it. A Shuffle display carrying a stale PinnedPath is NOT counted: it is not showing the
    /// clip because of that reference, so telling the user it will "go back to shuffling" would be
    /// claiming a consequence that does not happen.
    /// </summary>
    public static int CountReferences(WhalleeConfig config, string fileName) =>
        config.Displays.Count(a => a.Mode == DisplayMode.Pinned && Matches(a.PinnedPath, fileName));

    /// <summary>
    /// Clears every reference to the clip - pinned or merely stale - and returns how many were
    /// cleared. A Pinned assignment drops to Shuffle; one already on Shuffle keeps its mode and
    /// only loses the dangling path.
    /// </summary>
    public static int ClearAssignments(WhalleeConfig config, string fileName)
    {
        var cleared = 0;

        foreach (var assignment in config.Displays)
        {
            if (!Matches(assignment.PinnedPath, fileName)) continue;

            if (assignment.Mode == DisplayMode.Pinned) assignment.Mode = DisplayMode.Shuffle;
            assignment.PinnedPath = null;
            cleared++;
        }

        return cleared;
    }

    /// <summary>Device names of the displays pinned to the clip, for the confirm dialog's text.</summary>
    public static IReadOnlyList<string> PinnedDisplayNames(WhalleeConfig config, string fileName) =>
        config.Displays
            .Where(a => a.Mode == DisplayMode.Pinned && Matches(a.PinnedPath, fileName))
            .Select(a => a.DeviceName)
            .ToList();

    private static bool Matches(string? pinnedPath, string fileName) =>
        !string.IsNullOrWhiteSpace(pinnedPath) &&
        string.Equals(Path.GetFileName(pinnedPath), fileName, StringComparison.OrdinalIgnoreCase);
}
