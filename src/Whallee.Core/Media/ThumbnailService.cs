using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Whallee.Core.Media;

/// <summary>
/// Extracts and caches one still frame per clip for the library grid.
/// </summary>
/// <remarks>
/// EnsureThumbnailAsync launches ffmpeg and MUST NOT be allowed to run synchronously on a thread
/// that owns host windows - see the pump-thread rule. Callers on the UI thread must await it
/// without blocking; the process work happens off that thread.
/// </remarks>
public sealed class ThumbnailService
{
    private const int WidthPx = 480;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Used when the duration could not be probed. A second in is a safe guess.</summary>
    private static readonly TimeSpan FallbackOffset = TimeSpan.FromSeconds(1);

    /// <summary>Clears the opening frame, which is often black.</summary>
    private static readonly TimeSpan MinOffset = TimeSpan.FromSeconds(0.5);

    /// <summary>Stops a long clip seeking minutes in for a thumbnail.</summary>
    private static readonly TimeSpan MaxOffset = TimeSpan.FromSeconds(10);

    private readonly string _ffmpegPath;
    private readonly string _cacheFolder;

    public ThumbnailService(string ffmpegPath, string cacheFolder)
    {
        _ffmpegPath = ffmpegPath;
        _cacheFolder = cacheFolder;
    }

    public static string DefaultCacheFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whallee", "thumbs");

    /// <summary>Hash covers path AND last-write time, so an edited clip re-renders.</summary>
    public static string CacheFileName(string clipPath, DateTime lastWriteUtc)
    {
        var key = clipPath.ToLowerInvariant() + "|" +
                  lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + ".jpg";
    }

    public string CachePathFor(string clipPath, DateTime lastWriteUtc) =>
        Path.Combine(_cacheFolder, CacheFileName(clipPath, lastWriteUtc));

    /// <summary>ffprobe ships in the same directory as ffmpeg. Null when there is no directory.</summary>
    public static string? ProbePathFor(string ffmpegPath)
    {
        var directory = Path.GetDirectoryName(ffmpegPath);
        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "ffprobe.exe");
    }

    /// <summary>
    /// Where to take the frame from.
    /// </summary>
    /// <remarks>
    /// TEN PERCENT IN, BUT COMPUTED, NOT EXPRESSED AS A PERCENTAGE. ffmpeg rejects "-ss 10%"
    /// outright - "Invalid duration for option ss: 10%", verified by hand against a real clip - so
    /// the percentage has to be resolved against a duration probed first. Ten percent avoids the
    /// fade-from-black that a lot of stock footage opens with; the clamps stop it degenerating at
    /// either extreme. The upper bound on a very short clip is the important one: seeking past the
    /// end yields NO frame, so the clip would silently get no thumbnail at all.
    /// </remarks>
    public static TimeSpan SeekOffsetFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return FallbackOffset;

        var offset = duration * 0.10;
        if (offset < MinOffset) offset = MinOffset;
        if (offset > MaxOffset) offset = MaxOffset;

        // Never past the end, whatever the clamps above decided.
        var latest = duration * 0.9;
        return offset > latest ? latest : offset;
    }

    /// <summary>Returns the cached thumbnail path, generating it if needed. Null on failure.</summary>
    public async Task<string?> EnsureThumbnailAsync(string clipPath, CancellationToken ct)
    {
        if (!File.Exists(clipPath) || !File.Exists(_ffmpegPath)) return null;

        var stamp = File.GetLastWriteTimeUtc(clipPath);
        var target = CachePathFor(clipPath, stamp);
        if (File.Exists(target)) return target;   // cache hit: no ffprobe, no ffmpeg

        Directory.CreateDirectory(_cacheFolder);

        var offset = SeekOffsetFor(await ProbeDurationAsync(clipPath, ct).ConfigureAwait(false));

        // Write to a temp file then move, so a cancelled or crashed ffmpeg cannot leave a truncated
        // jpg that later looks like a valid cache hit.
        var temp = target + ".tmp";
        try
        {
            var exitCode = await RunAsync(
                _ffmpegPath, BuildArguments(clipPath, temp, offset), ct).ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(temp)) return null;
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (OperationCanceledException) { return null; }
        catch (IOException) { return null; }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>Duration via ffprobe, or Zero when it cannot be determined.</summary>
    private async Task<TimeSpan> ProbeDurationAsync(string clipPath, CancellationToken ct)
    {
        var probe = ProbePathFor(_ffmpegPath);
        if (probe is null || !File.Exists(probe)) return TimeSpan.Zero;

        try
        {
            var startInfo = NewStartInfo(probe);
            foreach (var arg in new[]
                     {
                         "-v", "error",
                         "-show_entries", "format=duration",
                         "-of", "default=noprint_wrappers=1:nokey=1",
                         clipPath
                     })
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null) return TimeSpan.Zero;

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await WaitAsync(process, ct).ConfigureAwait(false);

            // InvariantCulture: ffprobe always prints a dot, whatever the machine locale is.
            return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                   out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        }
        catch (OperationCanceledException) { return TimeSpan.Zero; }
        catch (Exception) { return TimeSpan.Zero; }   // a failed probe only costs us the fallback
    }

    private static async Task<int> RunAsync(
        string exe, IEnumerable<string> arguments, CancellationToken ct)
    {
        var startInfo = NewStartInfo(exe);
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        if (process is null) return -1;

        // Drained, not ignored: ffmpeg is chatty on stderr, and a full pipe buffer deadlocks it.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await WaitAsync(process, ct).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        return process.ExitCode;
    }

    private static async Task WaitAsync(Process process, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A hung ffmpeg must not be left running. The engine's job object does not own these,
            // so nothing else would ever clean them up.
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
    }

    private static ProcessStartInfo NewStartInfo(string exe) => new()
    {
        FileName = exe,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    /// <summary>
    /// -ss BEFORE -i seeks by keyframe, which is fast and plenty accurate for a thumbnail.
    /// The offset is formatted invariantly because ffmpeg wants a dot; a comma locale would emit
    /// "1,009" and be rejected.
    /// </summary>
    /// <remarks>
    /// -f image2 -c:v mjpeg ARE REQUIRED, not belt and braces. ffmpeg picks the muxer from the
    /// OUTPUT FILE EXTENSION, and this writes to a temp file whose extension is .tmp, so inference
    /// fails outright: "Unable to choose an output format for '...jpg.tmp'; use a standard
    /// extension for the filename or specify the format manually." Every clip failed this way and
    /// no unit test could see it, because none of them run ffmpeg. Naming the temp file so it ends
    /// in .jpg would also work, but pinning the format is better - it makes the invocation
    /// independent of what the file happens to be called.
    /// </remarks>
    private static IEnumerable<string> BuildArguments(
        string clipPath, string outputPath, TimeSpan offset) => new[]
    {
        "-nostdin", "-y",
        "-ss", offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-i", clipPath,
        "-frames:v", "1",
        "-vf", $"scale={WidthPx}:-1",
        "-f", "image2",
        "-c:v", "mjpeg",
        outputPath
    };
}
