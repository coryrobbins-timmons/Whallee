using System.Text;

namespace Whallee.Core.Displays;

/// <summary>
/// Reduces a set of displays to a comparable signature, so a caller can tell a real monitor-layout
/// change from a broadcast that reports one without anything having moved.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS: Windows broadcasts WM_DISPLAYCHANGE more often than the layout actually
/// changes. Observed live on 2026-08-31 — a broadcast arrives roughly 0.7 s into every startup,
/// with the layout identical before and after, which cost a full teardown and rebuild of all three
/// players on every launch. The Explorer branch of ShellWatcher had always guarded itself by
/// comparing the shell's pid; the display branch had no equivalent. This is that equivalent.
/// </remarks>
public static class DisplayTopology
{
    /// <summary>
    /// Order-independent, because the enumeration order is the OS's business and a reshuffle with
    /// identical geometry is not a topology change.
    ///
    /// Every field of <see cref="DisplayInfo"/> takes part deliberately. Position and size are the
    /// obvious ones; DeviceId matters because two near-identical panels differ only there on this
    /// machine, and IsPrimary matters because promoting a monitor to primary moves the virtual
    /// screen origin under everything without changing any single display's own bounds.
    /// </summary>
    public static string Signature(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) return "";

        var parts = new string[displays.Count];
        for (var i = 0; i < displays.Count; i++)
        {
            var d = displays[i];
            parts[i] = $"{d.DeviceId}|{d.DeviceName}|{d.X},{d.Y},{d.Width}x{d.Height}|{(d.IsPrimary ? 1 : 0)}";
        }

        Array.Sort(parts, StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            builder.Append(part);
            builder.Append(';');
        }
        return builder.ToString();
    }

    /// <summary>True when the layout genuinely differs and an attached client needs to re-attach.</summary>
    public static bool HasChanged(string previousSignature, IReadOnlyList<DisplayInfo> current) =>
        !string.Equals(previousSignature, Signature(current), StringComparison.Ordinal);
}
