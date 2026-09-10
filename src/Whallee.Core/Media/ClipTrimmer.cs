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
                    // KEPT FOR INSPECTION, under a name the cleanup below will not delete. A file
                    // that reached this point is the evidence for a bug that is otherwise
                    // impossible to reproduce on demand, and deleting it leaves only a log line.
                    var rejected = destinationPath + ".rejected";
                    try { File.Copy(destinationPath, rejected, overwrite: true); }
                    catch (Exception) { rejected = "(could not be kept)"; }

                    throw new InvalidOperationException(
                        $"The downloaded clip could not be read back: {check.Detail}. "
                        + $"Kept at {rejected} for diagnosis. "
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

    private static string LastLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
