namespace Whallee.Core.Media;

/// <summary>
/// Turns one line of yt-dlp's progress output into something worth putting on screen.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS NOW AND NOT BEFORE. Strategy A never used to succeed on this machine - yt-dlp
/// was two months stale and resolved every video through the android_vr client, which YouTube
/// serves about a minute of and then refuses - so every import fell through to strategy C in a few
/// seconds and nobody noticed that strategy A reports no progress. Updating yt-dlp on 2026-09-11
/// made strategy A the normal path, and a 5-minute 1440p section is now a multi-minute download
/// during which the status card said "Trying a direct section download…" and then sat there.
///
/// PARSED FROM A TEMPLATE WE SET, NOT FROM yt-dlp's DEFAULT OUTPUT. --progress-template pins the
/// fields and their order, so this does not have to scrape a human-readable progress bar whose
/// layout is not ours to control. <see cref="Marker"/> is what separates our line from every other
/// thing yt-dlp writes.
/// </remarks>
public static class DownloadProgress
{
    /// <summary>Prefix identifying a line this class emitted, rather than yt-dlp's own chatter.</summary>
    public const string Marker = "WHALLEE-PROGRESS";

    /// <summary>
    /// The <c>--progress-template</c> argument to pass to yt-dlp.
    /// </summary>
    /// <remarks>
    /// The field order here IS the parse order in <see cref="Describe"/>. Changing one without the
    /// other fails silently: the parser keeps compiling and the status card simply stops updating.
    /// A test pins the relationship.
    /// </remarks>
    public const string Template =
        "download:" + Marker + "|%(progress._percent_str)s|%(progress._speed_str)s";

    /// <summary>
    /// The line to show the user, or null if this is not a progress line or is unusable.
    /// </summary>
    /// <remarks>
    /// NULL FOR ANYTHING UNRECOGNISED, deliberately. The caller overwrites a single status line
    /// with whatever this returns, so letting a malformed fragment through would replace a useful
    /// message with noise. This parses another program's output; it must never throw.
    /// </remarks>
    public static string? Describe(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(Marker + "|", StringComparison.Ordinal)) return null;

        var fields = trimmed.Split('|');
        if (fields.Length < 3) return null;

        var percent = fields[1].Trim();
        if (percent.Length == 0) return null;

        var speed = fields[2].Trim();

        // Unknown on the first tick and whenever a transfer stalls. The percentage alone still
        // does the job it is here for - showing that the app is working rather than hung.
        var speedKnown = speed.Length > 0
            && !speed.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
            && !speed.Contains("N/A", StringComparison.OrdinalIgnoreCase);

        return speedKnown
            ? $"Downloading… {percent} at {speed}"
            : $"Downloading… {percent}";
    }
}
