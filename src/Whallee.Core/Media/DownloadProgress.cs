using System.Globalization;
using System.Text.RegularExpressions;

namespace Whallee.Core.Media;

/// <summary>
/// Turns ffmpeg's progress chatter into a line worth putting on screen during a section download.
/// </summary>
/// <remarks>
/// IT IS ffmpeg's PROGRESS, NOT yt-dlp's, and that is the whole reason this class was rewritten on
/// 2026-09-11. --download-sections delegates the actual transfer to ffmpeg, so yt-dlp's own
/// progress hook fires ONCE - observed emitting a single "100.0%" at the very end - and then sits
/// idle for the entire download. A --progress-template on yt-dlp therefore reports nothing useful;
/// the live numbers are on ffmpeg's stderr, as "time=00:00:03.25 ... speed=1.49x".
///
/// PERCENTAGE IS DERIVED, NOT REPORTED. ffmpeg knows how far through the section it is but not that
/// a section was requested, so it never prints a percentage. The caller knows the requested length,
/// and that is what turns an elapsed time into one.
///
/// WHY AN ETA RATHER THAN A TRANSFER RATE. The owner's complaint was that an import "took a longgg
/// while" with nothing on screen. A rate answers "how fast", which nobody asked; the useful answer
/// is how much longer. ffmpeg's speed multiplier gives it directly.
/// </remarks>
public static class DownloadProgress
{
    /// <summary>ffmpeg's encode speed relative to real time, e.g. "speed=1.49x".</summary>
    private static readonly Regex SpeedStat = new(
        @"speed=\s*([0-9]+(?:\.[0-9]+)?)x", RegexOptions.Compiled);

    /// <summary>
    /// The line to show, or null when <paramref name="chunk"/> carries no progress.
    /// </summary>
    /// <param name="chunk">Whatever arrived on ffmpeg's stderr since the last call.</param>
    /// <param name="sectionLength">The section length requested, used to derive a percentage.</param>
    /// <remarks>
    /// NULL FOR ANYTHING UNRECOGNISED. The caller overwrites one status line with the result, so a
    /// fragment must never replace a useful message. This parses another program's output and must
    /// never throw.
    ///
    /// ffmpeg rewrites its stats in place with carriage returns, so a chunk routinely holds several
    /// updates. Only the last matters - that is what ParseMeasuredLength already returns.
    /// </remarks>
    public static string? Describe(string? chunk, TimeSpan sectionLength)
    {
        if (string.IsNullOrEmpty(chunk)) return null;

        var elapsed = ClipContent.ParseMeasuredLength(chunk);
        if (elapsed is not { } done) return null;

        // Without a requested length there is no denominator. Still worth showing something: the
        // point of this line is that the app is visibly working.
        if (sectionLength <= TimeSpan.Zero)
            return $"Downloading… {Seconds(done)} so far";

        // CLAMPED. ffmpeg can overshoot slightly past the requested length on the final packet, and
        // "Downloading… 101%" reads as a bug.
        var fraction = done.TotalSeconds / sectionLength.TotalSeconds;
        var percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);

        var remaining = Remaining(chunk, sectionLength - done, fraction);
        return remaining is null
            ? $"Downloading… {percent}%"
            : $"Downloading… {percent}% — about {remaining} left";
    }

    /// <summary>
    /// Below this much progress, ffmpeg's speed figure is not worth believing.
    /// </summary>
    /// <remarks>
    /// MEASURED, NOT GUESSED. A real 2026-09-11 import opened with "about 49 minutes left", then
    /// 100, then 148, on a clip that finished in seconds: ffmpeg's speed multiplier starts near
    /// zero while it is still opening the stream, and dividing by it produces those numbers. A
    /// wildly wrong estimate is worse than none - it is exactly the "this is taking forever"
    /// impression this line was added to remove.
    /// </remarks>
    private const double EstimateFloor = 0.05;

    /// <summary>How much longer, phrased roughly, or null when it cannot be worked out.</summary>
    private static string? Remaining(string chunk, TimeSpan left, double fraction)
    {
        if (left <= TimeSpan.Zero) return null;
        if (fraction < EstimateFloor) return null;

        var matches = SpeedStat.Matches(chunk);
        if (matches.Count == 0) return null;

        // The last one, for the same reason as the timestamp: earlier values are stale.
        if (!double.TryParse(matches[^1].Groups[1].Value, NumberStyles.Float,
                             CultureInfo.InvariantCulture, out var speed) || speed <= 0)
            return null;

        var seconds = left.TotalSeconds / speed;

        // Deliberately coarse. A precise countdown on a figure this noisy invites the reader to
        // watch it disagree with itself.
        if (seconds < 45) return "a few seconds";
        if (seconds < 90) return "a minute";

        return $"{(int)Math.Round(seconds / 60)} minutes";
    }

    private static string Seconds(TimeSpan value)
    {
        var whole = (int)Math.Round(value.TotalSeconds);
        return whole == 1 ? "1 second" : $"{whole} seconds";
    }
}
