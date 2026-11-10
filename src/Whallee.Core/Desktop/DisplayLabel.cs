using System.Globalization;

namespace Whallee.Core.Desktop;

/// <summary>
/// "Display 2" for a device named \\.\DISPLAY2 - the number Windows shows in Settings › Display.
/// </summary>
/// <remarks>
/// DISPLAY ONLY. Config, pins and every comparison keep using the raw device name; this is just the
/// text a person reads. Anything that does not end in a number is shown as it is rather than guessed.
/// </remarks>
public static class DisplayLabel
{
    private const string Prefix = @"\\.\DISPLAY";

    public static string For(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return "Display";

        if (deviceName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(deviceName.AsSpan(Prefix.Length), NumberStyles.None,
                         CultureInfo.InvariantCulture, out var number))
            return $"Display {number}";

        return deviceName;
    }
}
