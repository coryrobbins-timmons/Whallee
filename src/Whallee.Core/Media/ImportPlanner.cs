using System.Text;

namespace Whallee.Core.Media;

/// <summary>
/// The arithmetic and naming rules behind a YouTube import. Pure by design, so all of it is tested.
/// </summary>
public static class ImportPlanner
{
    /// <summary>
    /// How much content is served before refusing, on the STRATEGY C fallback path only.
    /// </summary>
    /// <remarks>
    /// NOT A PROPERTY OF THE OWNER'S NETWORK, despite what this comment claimed until 2026-09-11.
    /// The cap belongs to YouTube's android_vr client. A stale yt-dlp (2026.07.04) resolved every
    /// video through that client, and googlevideo serves roughly a minute of such a URL and then
    /// answers 403 - confirmed by the refusal carrying "Server: gvs 1.0", and by a freshly resolved
    /// URL being refused on its FIRST request with nothing consumed. Other clients resolved by the
    /// same binary served byte 20 MiB of the same video without complaint.
    ///
    /// Updating yt-dlp to 2026.08.19 moved the default to a client that serves the whole file at
    /// full quality, which made strategy A succeed and start times real. This number therefore no
    /// longer binds a healthy import: it only sizes the prefix request on the strategy C fallback,
    /// and only once A has already failed for some other reason.
    ///
    /// KEPT RATHER THAN DELETED because C is still the fallback and still needs a sane request
    /// size. If C ever becomes the common path again, RE-MEASURE before trusting this.
    ///
    /// The original 2026-09-01 measurement, which remains sound for the android_vr case:
    /// A DURATION, NOT A BYTE COUNT, and that distinction is the whole point. Three measurements
    /// at ~1.0, ~3.8 and ~9.0 Mbps were served 6 MiB, 26 MiB and 64 MiB respectively - byte counts
    /// spanning more than 10x - but 51.0 s, 56.9 s and 59.8 s of content. Bracketing each between
    /// the bytes served and what the next chunk would have completed, all three intervals
    /// intersect at 59.8-61.3 s.
    ///
    /// The earlier model was a 6 MiB byte ceiling, which was simply the same 60 s allowance
    /// measured on a low-bitrate stream. DO NOT "correct" this back into a byte count at a larger
    /// value - that repeats the original mistake with a bigger number.
    ///
    /// The allowance is PER VIDEO: a second video requested immediately after a first was refused
    /// received a full fresh allowance on the same connection.
    /// </remarks>
    public static readonly TimeSpan ContentCeiling = TimeSpan.FromSeconds(60);

    /// <summary>Above this, the dialog demands a section instead of offering the whole video.</summary>
    public static readonly TimeSpan SectionRequiredAbove = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan DefaultSectionLength = TimeSpan.FromMinutes(5);

    private const int MaxSlugLength = 60;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

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
        if (slug.Length == 0) slug = "import";

        // Windows reserves certain device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9) and matches
        // them against the base name BEFORE the extension, so appending ".mp4" does not help —
        // File.Create("con.mp4") fails just as surely as "con". Prefix reserved names with
        // "import-" to produce a valid, readable filename.
        if (WindowsReservedNames.Contains(slug))
        {
            slug = "import-" + slug;
        }

        return slug + extension;
    }

    public static bool RequiresSection(TimeSpan duration) => duration > SectionRequiredAbove;

    /// <summary>
    /// How many seconds this network is expected to yield, shown BEFORE downloading.
    /// </summary>
    /// <remarks>
    /// Independent of bitrate and file size, which is the surprising part and the reason the old
    /// signature took clen. Telling the user up front is the difference between a known limit and
    /// an apparent failure.
    /// </remarks>
    public static TimeSpan PredictedObtainable(TimeSpan requested, TimeSpan videoDuration)
    {
        if (requested <= TimeSpan.Zero || videoDuration <= TimeSpan.Zero) return TimeSpan.Zero;

        var limit = ContentCeiling < videoDuration ? ContentCeiling : videoDuration;
        return requested < limit ? requested : limit;
    }

    /// <summary>Bytes to request in order to receive roughly <paramref name="seconds"/> of video.</summary>
    /// <remarks>
    /// clen/duration is an AVERAGE bitrate, so a variable-bitrate stream's opening seconds can be
    /// denser than the average. The margin covers that: over-requesting is free because the server
    /// simply refuses the surplus and the fetch loop stops, whereas under-requesting silently
    /// returns a shorter clip than the network would have allowed.
    /// </remarks>
    public static long BytesForSeconds(TimeSpan seconds, long clen, TimeSpan duration,
                                       double margin = 0.25)
    {
        if (seconds <= TimeSpan.Zero || clen <= 0 || duration <= TimeSpan.Zero) return 0;

        var fraction = seconds.TotalSeconds / duration.TotalSeconds * (1 + margin);
        var bytes = clen * fraction;
        return bytes >= clen ? clen : (long)bytes;
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
