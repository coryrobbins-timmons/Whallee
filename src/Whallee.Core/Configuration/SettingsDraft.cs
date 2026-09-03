namespace Whallee.Core.Configuration;

/// <summary>What the settings page wants one display to become.</summary>
/// <remarks>
/// PinnedFileName is a BARE FILE NAME, matching how LibraryViewModel.ApplyClip writes PinnedPath.
/// It is null for a Shuffle choice, and also for a Pinned choice on a display that is not playing
/// anything - see <see cref="SettingsDraft"/> for why that case is ignored rather than rejected.
/// </remarks>
public readonly record struct DisplayChoice(DisplayMode Mode, string? PinnedFileName);

/// <summary>
/// Turns the settings page's pending choices into config changes.
/// </summary>
/// <remarks>
/// PURE, AND DELIBERATELY SO. This is the whole testable surface of the settings page - the page
/// itself, the engine restart and the shortcut write are all outside standing ruling 3's boundary.
/// Keeping the decisions here means the part that can be wrong is the part with coverage.
/// </remarks>
public static class SettingsDraft
{
    /// <summary>
    /// Which clip a display's pin dropdown should start on: its saved pin, else whatever it happens
    /// to be playing, else nothing.
    /// </summary>
    /// <remarks>
    /// THE SAVED PIN WINS OVER WHAT IS PLAYING, and the order is the point. A pinned display that
    /// has fallen back to a random clip - because its pinned file was deleted or has not synced
    /// yet, which ClipResolver.Resolve does deliberately - is still *configured* for the pin the
    /// user chose. Showing the fallback would quietly offer to overwrite their choice with an
    /// accident.
    ///
    /// EXISTS BECAUSE COUPLING PINNING TO PLAYBACK WAS A BUG. The settings page used to decide
    /// whether a display could be pinned at all from WallpaperEngine.ActivePlayers, which is EMPTY
    /// for the whole of a restart - StopAll clears it before StartOnce refills it - and a restart
    /// pumps messages, so the UI is live and interactive during that window. A fullscreen app
    /// changing display mode fires WM_DISPLAYCHANGE, the shell watcher restarts the engine, and any
    /// row built in that window came up "(not playing)" with its Pinned option disabled until the
    /// user navigated away and back. Owner-reported 2026-09-03.
    ///
    /// Both inputs may be null and null is a valid answer: an empty library, or a display that has
    /// never been pinned and is not playing. A Pinned choice carrying null is ignored by Apply
    /// rather than written, so nothing downstream needs to guard against it.
    /// </remarks>
    public static string? InitialPin(string? savedPinnedPath, string? nowPlaying)
    {
        if (!string.IsNullOrWhiteSpace(savedPinnedPath)) return Path.GetFileName(savedPinnedPath);

        return string.IsNullOrWhiteSpace(nowPlaying) ? null : Path.GetFileName(nowPlaying);
    }

    /// <summary>True when applying these values would change anything in config.</summary>
    public static bool HasChanges(
        WhalleeConfig config, ScalingMode scaling,
        IReadOnlyDictionary<string, DisplayChoice> displays)
    {
        if (config.Scaling != scaling) return true;

        foreach (var assignment in config.Displays)
            if (TryChoice(displays, assignment.DeviceName, out var choice) &&
                Changes(assignment, choice))
                return true;

        return false;
    }

    /// <summary>Writes the values into config. Returns how many fields changed.</summary>
    public static int Apply(
        WhalleeConfig config, ScalingMode scaling,
        IReadOnlyDictionary<string, DisplayChoice> displays)
    {
        var changed = 0;

        if (config.Scaling != scaling)
        {
            config.Scaling = scaling;
            changed++;
        }

        foreach (var assignment in config.Displays)
        {
            if (!TryChoice(displays, assignment.DeviceName, out var choice)) continue;
            if (!Changes(assignment, choice)) continue;

            if (choice.Mode == DisplayMode.Shuffle)
            {
                assignment.Mode = DisplayMode.Shuffle;
                assignment.PinnedPath = null;
            }
            else
            {
                assignment.Mode = DisplayMode.Pinned;
                assignment.PinnedPath = choice.PinnedFileName;
            }

            changed++;
        }

        return changed;
    }

    /// <summary>Would this choice actually alter the assignment?</summary>
    /// <remarks>
    /// A PINNED CHOICE WITH NO FILE NAME IS NOT A CHANGE, deliberately. It is the "pin a display
    /// that is not playing anything" case. The page disables that option, but the rule belongs here
    /// too: writing Mode = Pinned with a null PinnedPath produces an assignment ClipResolver treats
    /// as unpinned anyway, while the UI would claim it was pinned. Silently doing nothing is the
    /// honest outcome, and it spares the caller an error it could not act on.
    ///
    /// A SHUFFLE CHOICE STILL CLEARS A STALE PinnedPath, even on a display already in Shuffle mode -
    /// a dangling pin is worth tidying - but it must not touch the mode.
    /// </remarks>
    private static bool Changes(DisplayAssignment assignment, DisplayChoice choice)
    {
        if (choice.Mode == DisplayMode.Shuffle)
            return assignment.Mode != DisplayMode.Shuffle || assignment.PinnedPath is not null;

        if (string.IsNullOrWhiteSpace(choice.PinnedFileName)) return false;

        return assignment.Mode != DisplayMode.Pinned ||
               !string.Equals(assignment.PinnedPath, choice.PinnedFileName,
                              StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Case-insensitive device-name lookup.
    /// </summary>
    /// <remarks>
    /// Walks the dictionary rather than calling TryGetValue because IReadOnlyDictionary carries no
    /// comparer of its own: a caller that built an ordinal dictionary would miss "\\.\DISPLAY6"
    /// against "\\.\display6", and DisplayEnumerator's casing is not something this code controls.
    /// Three displays make the walk free.
    /// </remarks>
    private static bool TryChoice(
        IReadOnlyDictionary<string, DisplayChoice> displays, string deviceName,
        out DisplayChoice choice)
    {
        foreach (var pair in displays)
        {
            if (!string.Equals(pair.Key, deviceName, StringComparison.OrdinalIgnoreCase)) continue;

            choice = pair.Value;
            return true;
        }

        choice = default;
        return false;
    }
}
