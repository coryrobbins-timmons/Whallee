using System.Globalization;
using Whallee.Core.Displays;

namespace Whallee.Core.Desktop;

/// <summary>
/// "Display 2" - the number Windows shows for that screen in Settings › Display.
/// </summary>
/// <remarks>
/// DISPLAY ONLY. Config, pins and every comparison keep using the raw device name; this is just the
/// text a person reads.
///
/// The number is the screen's position in Windows' own display order (WindowsDisplayOrder). Only
/// when that is unavailable does it fall back to the digits in \\.\DISPLAYn - which can skip
/// numbers on a dock (1, 5, 6), and is why it is the fallback rather than the answer.
/// </remarks>
public static class DisplayLabel
{
    private const string Prefix = @"\\.\DISPLAY";

    /// <summary>Reads Windows' current order and labels one device.</summary>
    public static string For(string? deviceName) => For(deviceName, WindowsDisplayOrder.Read());

    /// <param name="windowsOrder">GDI device names in Settings' order; empty when unknown.</param>
    public static string For(string? deviceName, IReadOnlyList<string> windowsOrder)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "Display";

        for (var i = 0; i < windowsOrder.Count; i++)
            if (string.Equals(windowsOrder[i], deviceName, StringComparison.OrdinalIgnoreCase))
                return $"Display {i + 1}";

        if (deviceName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(deviceName.AsSpan(Prefix.Length), NumberStyles.None,
                         CultureInfo.InvariantCulture, out var number))
            return $"Display {number}";

        return deviceName;
    }
}
