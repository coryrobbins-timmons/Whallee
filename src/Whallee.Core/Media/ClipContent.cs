using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Whallee.Core.Media;

/// <summary>
/// Measures how much video a file ACTUALLY holds, as opposed to how much it claims to.
/// </summary>
/// <remarks>
/// WHY AN ESTIMATE WAS NOT ENOUGH. A strategy-C prefix is the opening bytes of a much longer MP4,
/// and this one carried its index at the FRONT — so ffprobe read the truncated 2 MiB file and
/// reported a duration of 9061 s, the length of the whole 2½-hour video. Every cheap way of asking
/// "how long is this file" answers with the header's claim, which is a lie by construction here.
///
/// ImportPlanner.TrimTargetFor answered it a second way, converting bytes to seconds at the
/// video's AVERAGE bitrate. That is a real prediction but an optimistic one: this video's opening
/// is about 15% denser than its average, so 2 MiB held 33.62 s where the average implied 38.8 s.
/// The 5% margin could not cover the gap, ffmpeg was asked for 36.9 s, it hit EOF mid-stream and
/// exited WITHOUT writing the output index — the "moov atom not found" file the owner could not
/// play. Raising the margin would only move the number at which the same bug reappears.
///
/// So this decodes the file and reports where it really ran out. The pass is -c copy into a null
/// muxer: no re-encode, no output written, ~20 ms of ffmpeg work on a 2 MiB prefix.
/// </remarks>
public static class ClipContent
{
    /// <summary>
    /// How far short of the measurement to stop.
    /// </summary>
    /// <remarks>
    /// MEASURED, NOT GUESSED, on the rejected prefix from 2026-09-10: -t 33.62 (exactly what
    /// ffmpeg reported) still produced "moov atom not found", while -t 33.50 produced a clean
    /// file. Reaching the final packet is itself enough to trip the truncated-input path, so the
    /// target has to sit strictly inside the measurement. 0.25 s is double the 0.12 s that
    /// verified clean, and costs well under 1% of a ~34 s clip.
    /// </remarks>
    public static readonly TimeSpan Safety = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// ffmpeg rewrites its stats line in place with a carriage return, so a drained stderr holds
    /// every intermediate value and only the last one is the length reached.
    /// </summary>
    private static readonly Regex TimeStat = new(
        @"time=(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// The last content length ffmpeg reported, or null if it never reported one. Pure.
    /// </summary>
    /// <remarks>
    /// NULL MEANS "NOT MEASURED", NEVER ZERO. Zero would read as an empty file and fail an import
    /// that the caller can still complete from its estimate — the same distinction ClipIntegrity
    /// draws between "broken" and "we could not tell".
    /// </remarks>
    public static TimeSpan? ParseMeasuredLength(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;

        TimeSpan? last = null;

        foreach (Match match in TimeStat.Matches(stderr))
        {
            var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            // ffmpeg prints a huge placeholder (time=-577014:32:22.77) before the first real
            // timestamp is known. The regex cannot match its minus sign, but the hour count alone
            // is enough to reject it, and it also rules out a value no prefix could legitimately
            // reach.
            if (hours > 24) continue;

            last = new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
        }

        return last;
    }

    /// <summary>How much of a measured length is safe to ask ffmpeg for. Pure.</summary>
    public static TimeSpan UsableLength(TimeSpan measured)
    {
        var usable = measured - Safety;
        return usable > TimeSpan.Zero ? usable : TimeSpan.Zero;
    }

    /// <summary>
    /// Decodes <paramref name="path"/> to find where it really ends. Null when the measurement
    /// could not be taken; never throws.
    /// </summary>
    public static async Task<TimeSpan?> MeasureAsync(
        string ffmpegPath, string path, CancellationToken ct = default)
    {
        if (!File.Exists(ffmpegPath) || !File.Exists(path)) return null;

        try
        {
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            // -stats forces the progress line even at error log level; it is the only thing here
            // that is actually being read. -f null discards the output rather than writing one.
            foreach (var arg in new[]
                     { "-v", "error", "-stats", "-i", path, "-c", "copy", "-f", "null", "-" })
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            // Drained, not ignored: a full pipe buffer deadlocks the child.
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }

            // A NON-ZERO EXIT IS STILL WORTH READING. The whole point is measuring a file ffmpeg
            // considers damaged, and on the prefix this was written for it complains about a
            // partial file and then exits 0 anyway. What matters is whether it got far enough to
            // report a time, not how it felt about the input.
            return ParseMeasuredLength(await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { return null; }
    }
}
