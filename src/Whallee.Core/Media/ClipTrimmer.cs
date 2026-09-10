using System.Diagnostics;
using System.Globalization;

namespace Whallee.Core.Media;

/// <summary>Cuts a local file down to a usable clip with ffmpeg. Never touches the network.</summary>
/// <remarks>
/// -c copy, so this is a remux and not a re-encode: seconds rather than minutes, and no quality
/// loss. The caller MUST pass a length computed by ImportPlanner.TrimTargetFor, which is
/// deliberately shorter than what arrived - asking for more than the file holds makes ffmpeg hit
/// EOF and exit without finalising, producing "moov atom not found".
///
/// This method throws on any failure: ArgumentOutOfRangeException for invalid input, or
/// InvalidOperationException for runtime failures. Returns normally on success.
///
/// Note: -ss before -i seeks to the start time, but the actual output start may not align
/// exactly with the requested time if the nearest keyframe is earlier. This is expected behavior
/// for fast seeking with -c copy.
/// </remarks>
public static class ClipTrimmer
{
    public static async Task TrimAsync(
        string ffmpegPath,
        string sourcePath,
        string destinationPath,
        TimeSpan length,
        TimeSpan start = default,
        CancellationToken ct = default,
        IProgress<string>? progress = null)
    {
        if (length <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(length),
                "Trim length must be greater than zero.");

        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // -ss BEFORE -i is the fast seek. -y overwrites a leftover from a previous attempt.
        info.ArgumentList.Add("-y");
        if (start > TimeSpan.Zero)
        {
            info.ArgumentList.Add("-ss");
            info.ArgumentList.Add(start.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(sourcePath);
        info.ArgumentList.Add("-t");
        info.ArgumentList.Add(length.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("copy");
        info.ArgumentList.Add(destinationPath);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                throw new InvalidOperationException("ffmpeg could not be started.");

            try
            {
                // Drain stderr while waiting: ffmpeg is chatty and a full pipe buffer deadlocks the child.
                var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                if (process.ExitCode != 0 || !File.Exists(destinationPath) ||
                    new FileInfo(destinationPath).Length == 0)
                {
                    throw new InvalidOperationException(
                        "ffmpeg could not build a clip from the downloaded prefix. " +
                        LastLine(stderr));
                }

                // EXIT CODE 0 IS NOT PROOF THE FILE PLAYS. On 2026-09-09 an import reported
                // success and put a file in the library that mpv refused on all three displays;
                // ffprobe's verdict on it was "moov atom not found" - the MP4 index lives at the
                // END of the file and was never written. A truncated MP4 still has a non-zero
                // length and ffmpeg had already exited 0, so the checks above cannot see it.
                //
                // ONLY POSITIVE EVIDENCE REJECTS. The first attempt at this used
                // MediaProbe.ProbeAsync, which collapses every failure into "no duration" - so a
                // probe that timed out looked exactly like a broken file, and on 2026-09-10 the
                // owner could not import anything at all. ClipIntegrity separates "ffprobe read it
                // and rejected it" from "the check told us nothing", and only the former is grounds
                // to fail an import.
                var check = await ClipIntegrity.VerifyAsync(ffmpegPath, destinationPath, ct)
                    .ConfigureAwait(false);

                if (check.Verdict == ClipReadability.Unreadable)
                {
                    var kept = Quarantine(sourcePath, destinationPath);

                    throw new InvalidOperationException(
                        $"The downloaded clip could not be read back: {check.Detail}. "
                        + $"Kept at {kept} for diagnosis. "
                        + LastLine(stderr));
                }

                progress?.Report(check.Verdict == ClipReadability.Readable
                    ? $"Checked the clip: {check.Detail}."
                    : $"Could not confirm the clip is playable ({check.Detail}); accepting it anyway.");
            }
            finally
            {
                // Kill the child if cancellation orphaned it, before we delete the output file - a
                // live ffmpeg keeps writing, and a delete races the writer.
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
            }
        }
        catch
        {
            // A failed or cancelled trim must not leave a partially written or truncated file in the
            // user's video library - it would be indistinguishable from a real clip until played.
            try { File.Delete(destinationPath); } catch (Exception) { }
            throw;
        }
    }

    /// <summary>Where rejected clips are kept. OUTSIDE the import staging folder, deliberately.</summary>
    /// <remarks>
    /// The first attempt at retention copied the bad file to "&lt;destination&gt;.rejected" and claimed
    /// the cleanup would not delete it. It did: YouTubeImporter.ImportAsync removes the WHOLE
    /// staging directory in a finally block, so every copy was destroyed on the way out and three
    /// separate failures on 2026-09-10 left nothing but a log line. Anything kept for diagnosis has
    /// to live somewhere no import owns.
    /// </remarks>
    public static string QuarantineFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whallee", "rejected");

    /// <summary>
    /// Keeps the unreadable output AND the input it came from, and reports where they went.
    /// </summary>
    /// <remarks>
    /// BOTH FILES, because either one alone is ambiguous. An output with no index can mean the
    /// input was already broken, or that we asked ffmpeg for more than the input held; only having
    /// the source next to it distinguishes those.
    /// </remarks>
    private static string Quarantine(string sourcePath, string destinationPath)
    {
        try
        {
            Directory.CreateDirectory(QuarantineFolder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var kept = Path.Combine(QuarantineFolder, $"{stamp}-{Path.GetFileName(destinationPath)}");
            File.Copy(destinationPath, kept, overwrite: true);

            try
            {
                if (File.Exists(sourcePath))
                    File.Copy(sourcePath,
                        Path.Combine(QuarantineFolder, $"{stamp}-{Path.GetFileName(sourcePath)}"),
                        overwrite: true);
            }
            catch (Exception) { /* the output is the important half; keep it either way */ }

            return kept;
        }
        catch (Exception)
        {
            return "(could not be kept)";
        }
    }

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
