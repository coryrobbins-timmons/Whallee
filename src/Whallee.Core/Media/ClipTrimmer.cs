using System.Diagnostics;
using System.Globalization;

namespace Whallee.Core.Media;

/// <summary>Cuts a local file down to a usable clip with ffmpeg. Never touches the network.</summary>
/// <remarks>
/// -c copy, so this is a remux and not a re-encode: seconds rather than minutes, and no quality
/// loss. The caller MUST pass a length computed by ImportPlanner.TrimTargetFor, which is
/// deliberately shorter than what arrived - asking for more than the file holds makes ffmpeg hit
/// EOF and exit without finalising, producing "moov atom not found".
/// </remarks>
public static class ClipTrimmer
{
    public static async Task<bool> TrimAsync(
        string ffmpegPath,
        string sourcePath,
        string destinationPath,
        TimeSpan length,
        TimeSpan start = default,
        CancellationToken ct = default)
    {
        if (length <= TimeSpan.Zero) return false;

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
            if (process is null) return false;

            try
            {
                // Drain stderr while waiting: ffmpeg is chatty and a full pipe buffer deadlocks the child.
                var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                if (process.ExitCode == 0 && File.Exists(destinationPath) &&
                    new FileInfo(destinationPath).Length > 0)
                    return true;

                throw new InvalidOperationException(
                    "ffmpeg could not build a clip from the downloaded prefix. " +
                    LastLine(stderr));
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
