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
        CancellationToken ct = default)
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

                // EXIT CODE 0 IS NOT PROOF THE FILE PLAYS, and this is not theoretical: on
                // 2026-09-09 an import reported success and put a file in the library that mpv
                // refused on all three displays. ffprobe's verdict on it was "moov atom not found"
                // - the MP4 index, which lives at the END of the file, was never written.
                //
                // That is the exact failure this class's own remarks warn about, and the old check
                // could not see it: a truncated MP4 has a non-zero length and ffmpeg had already
                // exited 0. The only honest test is to try to read the thing back.
                //
                // Probing costs one ffprobe run on a local file. An import that quietly adds an
                // unplayable clip costs the user a tile that looks real until they apply it.
                var probed = await MediaProbe.ProbeAsync(ffmpegPath, destinationPath, ct)
                    .ConfigureAwait(false);

                if (probed.Duration <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException(
                        "The download was cut short, so the clip has no index and cannot be "
                        + "played. Try again, or ask for a shorter section. "
                        + LastLine(stderr));
                }
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
