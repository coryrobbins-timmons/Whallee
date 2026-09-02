using System.Diagnostics;
using System.Globalization;

namespace Whallee.Core.Media;

/// <summary>What the Details dialog shows about a clip. Zero means "not determined".</summary>
public readonly record struct MediaInfo(TimeSpan Duration, int Width, int Height)
{
    public static MediaInfo None => new(TimeSpan.Zero, 0, 0);

    public bool HasDuration => Duration > TimeSpan.Zero;
    public bool HasResolution => Width > 0 && Height > 0;
}

/// <summary>
/// Duration and resolution for one clip, via ffprobe.
/// </summary>
/// <remarks>
/// SPLIT INTO A PURE PARSER AND A PROCESS LAUNCH ON PURPOSE. Parse() is the half that can be unit
/// tested; ProbeAsync() is the half standing ruling 3 keeps out of the suite. Bugs live in the
/// parsing, so that is where the coverage goes.
///
/// NEVER call ProbeAsync on a thread that owns host windows without Task.Run around it - it starts
/// a process and reads its output. See the pump-thread rule.
/// </remarks>
public static class MediaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Parses ffprobe's <c>key=value</c> output. Pure.</summary>
    /// <remarks>
    /// KEYED OUTPUT, NOT THE nokey FORM. "-of default=noprint_wrappers=1" prints "width=1920"
    /// rather than a bare "1920", which makes the parse independent of the order ffprobe happens to
    /// emit its stream and format sections in. The bare form saves nothing and would break silently
    /// if that order ever changed.
    ///
    /// InvariantCulture on the duration: ffprobe always prints a dot, whatever the machine locale
    /// is. Same reasoning as ThumbnailService.ProbeDurationAsync.
    /// </remarks>
    public static MediaInfo Parse(string? ffprobeOutput)
    {
        if (string.IsNullOrWhiteSpace(ffprobeOutput)) return MediaInfo.None;

        var duration = TimeSpan.Zero;
        var width = 0;
        var height = 0;

        foreach (var line in ffprobeOutput.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            // Trim covers the trailing \r of CRLF output as well as any stray padding.
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Equals("width", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
            else if (key.Equals("height", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
            else if (key.Equals("duration", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out var seconds) && seconds > 0)
                duration = TimeSpan.FromSeconds(seconds);
        }

        return new MediaInfo(duration, width > 0 ? width : 0, height > 0 ? height : 0);
    }

    /// <summary>Runs ffprobe. Returns <see cref="MediaInfo.None"/> on any failure; never throws.</summary>
    public static async Task<MediaInfo> ProbeAsync(
        string ffmpegPath, string clipPath, CancellationToken ct)
    {
        var probe = ThumbnailService.ProbePathFor(ffmpegPath);
        if (probe is null || !File.Exists(probe) || !File.Exists(clipPath)) return MediaInfo.None;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = probe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var arg in new[]
                     {
                         "-v", "error",
                         "-select_streams", "v:0",
                         "-show_entries", "stream=width,height:format=duration",
                         "-of", "default=noprint_wrappers=1",
                         clipPath
                     })
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null) return MediaInfo.None;

            // Drained, not ignored: a full pipe buffer deadlocks the child. Same as ThumbnailService.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A hung ffprobe must not be left running: nothing else would ever clean it up.
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return MediaInfo.None;
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return Parse(await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) { return MediaInfo.None; }
        catch (Exception) { return MediaInfo.None; }   // a failed probe only costs us two dashes
    }
}
