using System.Text;

namespace Whallee.Core.Media;

/// <summary>
/// The arithmetic and naming rules behind a YouTube import. Pure by design, so all of it is tested.
/// </summary>
public static class ImportPlanner
{
    /// <summary>
    /// How many bytes this network will hand over before returning 403, measured 2026-08-19.
    /// </summary>
    /// <remarks>
    /// AN OBSERVATION FROM ONE VIDEO, NOT A LAW. Three 2 MiB range requests succeeded and the
    /// fourth failed, persistently, unaffected by re-resolving the URL or waiting two minutes.
    /// Whether the allowance is per-video or per-IP is UNMEASURED - see the harness `budget` verb.
    /// </remarks>
    public const long ByteCeiling = 6 * 1024 * 1024;

    /// <summary>Above this, the dialog demands a section instead of offering the whole video.</summary>
    public static readonly TimeSpan SectionRequiredAbove = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan DefaultSectionLength = TimeSpan.FromMinutes(5);

    private const int MaxSlugLength = 60;

    /// <summary>Turns a video title into a safe library filename.</summary>
    public static string SlugFor(string title, string extension = ".mp4")
    {
        var builder = new StringBuilder(title.Length);
        var lastWasSeparator = true;   // leading separators are dropped

        foreach (var character in title)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxSlugLength) slug = slug[..MaxSlugLength].TrimEnd('-');

        // A title of pure punctuation would otherwise produce a bare ".mp4", which is a hidden
        // file on every platform and an unclickable tile here.
        return (slug.Length == 0 ? "import" : slug) + extension;
    }

    public static bool RequiresSection(TimeSpan duration) => duration > SectionRequiredAbove;

    /// <summary>
    /// How many seconds the byte ceiling is expected to yield, shown BEFORE downloading.
    /// </summary>
    /// <remarks>
    /// The ceiling is measured in bytes, so the usable length depends entirely on bitrate: about
    /// 45-50 s of 1 Mbps ambient footage, but only ~10 s of a detailed 5 Mbps 1080p source. Telling
    /// the user up front is the difference between a known limit and an apparent failure.
    /// </remarks>
    public static TimeSpan PredictedObtainable(
        long clen, TimeSpan duration, long byteCeiling = ByteCeiling)
    {
        if (clen <= 0 || duration <= TimeSpan.Zero) return TimeSpan.Zero;

        var seconds = Math.Min((double)byteCeiling / clen * duration.TotalSeconds,
                               duration.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The duration to hand ffmpeg after a prefix fetch - deliberately SHORTER than what arrived.
    /// </summary>
    /// <remarks>
    /// ASKING FOR MORE THAN ARRIVED IS THE "moov atom not found" BUG. ffmpeg hits EOF and exits
    /// without finalising the container, leaving a file with no header. Trimming to slightly less
    /// lets the muxer finish and write a valid moov.
    /// </remarks>
    public static TimeSpan TrimTargetFor(
        long bytesFetched, long clen, TimeSpan duration, double marginFraction = 0.05)
    {
        if (bytesFetched <= 0 || clen <= 0 || duration <= TimeSpan.Zero) return TimeSpan.Zero;

        var available = (double)bytesFetched / clen * duration.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, available * (1 - marginFraction)));
    }

    /// <summary>Null when the section is usable, otherwise the reason to show the user.</summary>
    public static string? ValidateSection(TimeSpan start, TimeSpan length, TimeSpan duration)
    {
        if (start < TimeSpan.Zero) return "Start time cannot be negative.";
        if (length <= TimeSpan.Zero) return "Length must be greater than zero.";
        if (start >= duration)
            return $"Start time is past the end of the video ({Format(duration)}).";
        if (start + length > duration)
            return $"That section runs past the end of the video ({Format(duration)}).";
        return null;
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
}
