namespace Whallee.Core.Media;

/// <summary>
/// Shared helper for reading the system PATH environment variable.
/// </summary>
internal static class EnvironmentPath
{
    /// <summary>
    /// Reads the system PATH and returns its directories.
    /// </summary>
    /// <remarks>
    /// Broad exception handling: if PATH cannot be read or parsed (permissions, format issues, etc.),
    /// returns an empty list and allows discovery to fall back to the winget glob. This graceful
    /// degradation is critical for robustness — PATH reading is not essential, it's an optimization.
    /// </remarks>
    public static IReadOnlyList<string> Directories()
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
