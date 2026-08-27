using Whallee.Core.Configuration;
using Whallee.Core.Displays;

namespace Whallee.Core;

/// <summary>
/// The rule that decides which saved assignment belongs to an observed display: stable device id
/// first, then device name.
/// </summary>
/// <remarks>
/// Extracted from <see cref="WallpaperEngine"/> because it is a Global Constraint expressed as
/// pure logic — no Win32, no I/O — and therefore the one part of the display path that can be
/// unit-tested honestly. <see cref="WallpaperEngine"/> keeps only the thin wrapper that applies
/// the "add to config" side effect.
/// </remarks>
public static class DisplayMatcher
{
    /// <summary>
    /// Finds <paramref name="display"/>'s assignment among <paramref name="saved"/>, or builds a
    /// Shuffle default for it.
    /// </summary>
    /// <remarks>
    /// Never mutates <paramref name="saved"/>. That is deliberate on both halves: a saved
    /// assignment whose monitor is currently absent must stay in config untouched so it comes back
    /// when the monitor does, and whether a newly created assignment gets persisted is the
    /// caller's decision, not this method's.
    /// </remarks>
    /// <returns>
    /// The assignment to use, and whether it was newly created — i.e. is not yet in config.
    /// </returns>
    public static (DisplayAssignment Assignment, bool Created) Resolve(
        IReadOnlyList<DisplayAssignment> saved, DisplayInfo display)
    {
        // DeviceId is the stable per-panel identifier and wins outright. DeviceName (\\.\DISPLAY6)
        // is only a slot number that Windows hands to whichever panel currently occupies the slot,
        // so it is a fallback for assignments saved before an id was recorded — never a
        // tie-breaker against a device id that already matched.
        var match =
            saved.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.DeviceId) &&
                string.Equals(d.DeviceId, display.DeviceId, StringComparison.OrdinalIgnoreCase))
            ?? saved.FirstOrDefault(d =>
                string.Equals(d.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return (match, false);

        return (new DisplayAssignment
        {
            DeviceId = display.DeviceId,
            DeviceName = display.DeviceName,
            Mode = DisplayMode.Shuffle
        }, true);
    }
}
