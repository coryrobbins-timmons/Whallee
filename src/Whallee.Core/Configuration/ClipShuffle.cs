namespace Whallee.Core.Configuration;

/// <summary>
/// "Shuffle" and "Daily Shuffle": pin a random clip to every display.
/// </summary>
/// <remarks>
/// PINS, NOT DisplayMode.Shuffle. A Shuffle-mode display re-rolls on every engine start - every
/// Explorer restart, display change and logon - so "today's wallpaper" would not survive the day.
/// Pinning the pick keeps it until the next shuffle, which is what a daily shuffle means.
///
/// Pure: no clock, no disk. The caller passes today and the library listing, so all of it is tested.
/// </remarks>
public static class ClipShuffle
{
    /// <summary>Whether a daily shuffle is due: on, and nothing has shuffled yet today.</summary>
    /// <remarks>
    /// "Before today", not "not today": a clock set backwards must not shuffle on every start.
    /// </remarks>
    public static bool IsDailyDue(bool enabled, DateOnly? lastShuffle, DateOnly today) =>
        enabled && (lastShuffle is not { } last || last < today);

    /// <summary>
    /// Pins a random clip to each display and records today. Returns false, changing nothing,
    /// when the library is empty or there are no displays.
    /// </summary>
    /// <param name="libraryFiles">Full paths or bare names; only the file name is stored.</param>
    /// <param name="deviceName">
    /// Shuffle only this display (the page's "Apply to" choice); null shuffles all of them.
    /// </param>
    /// <remarks>
    /// DIFFERENT CLIPS PER DISPLAY while the library has enough of them - three monitors showing the
    /// same video reads as "it didn't shuffle". AVOIDS EACH DISPLAY'S CURRENT CLIP where it can, for
    /// the same reason: a shuffle that lands on what was already there looks like a dead button.
    ///
    /// A ONE-DISPLAY SHUFFLE ALSO AVOIDS WHAT THE OTHER DISPLAYS ARE PINNED TO, and does not record
    /// today: it is not the daily all-display shuffle, so it must not stand in for one.
    /// </remarks>
    public static bool Apply(
        WhalleeConfig config, IReadOnlyList<string> libraryFiles, Random random, DateOnly today,
        string? deviceName = null)
    {
        var names = libraryFiles.Select(Path.GetFileName).OfType<string>()
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var targets = config.Displays
            .Where(d => deviceName is null ||
                        string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (names.Count == 0 || targets.Count == 0) return false;

        var used = new HashSet<string>(
            config.Displays.Except(targets)
                  .Where(d => d.Mode == DisplayMode.Pinned && d.PinnedPath is not null)
                  .Select(d => d.PinnedPath!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var display in targets)
        {
            var fresh = names.Where(n => !used.Contains(n) &&
                                         !string.Equals(n, display.PinnedPath, StringComparison.OrdinalIgnoreCase))
                             .ToList();

            // Falls back step by step rather than failing: unused-but-current, then anything.
            if (fresh.Count == 0) fresh = names.Where(n => !used.Contains(n)).ToList();
            if (fresh.Count == 0) fresh = names;

            var pick = fresh[random.Next(fresh.Count)];
            used.Add(pick);

            display.Mode = DisplayMode.Pinned;
            display.PinnedPath = pick;
        }

        if (deviceName is null) config.LastShuffleDate = today;
        return true;
    }
}
