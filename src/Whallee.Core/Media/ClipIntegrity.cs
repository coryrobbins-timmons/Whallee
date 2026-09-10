using System.Diagnostics;
using System.Globalization;

namespace Whallee.Core.Media;

/// <summary>What a readability check could actually establish about a file.</summary>
public enum ClipReadability
{
    /// <summary>ffprobe read it and it has a real duration.</summary>
    Readable,

    /// <summary>ffprobe read it and rejected it. Positive evidence the file is broken.</summary>
    Unreadable,

    /// <summary>The check itself did not run or said nothing useful. NOT evidence of anything.</summary>
    Unknown
}

/// <summary>The verdict plus whatever ffprobe said about it.</summary>
public readonly record struct ClipCheck(ClipReadability Verdict, string Detail);

/// <summary>
/// Asks ffprobe whether a file can actually be read back.
/// </summary>
/// <remarks>
/// THE THREE-WAY VERDICT IS THE WHOLE POINT, and it exists because a two-way one shipped a bug on
/// 2026-09-09. That version verified imports with <see cref="MediaProbe.ProbeAsync"/>, which is
/// documented to return <see cref="MediaInfo.None"/> on ANY failure and never throw — a probe that
/// timed out, or could not find ffprobe, was indistinguishable from a genuinely broken file. Every
/// such hiccup became "your import failed", and the owner could not import anything at all.
///
/// Absence of evidence is not evidence of breakage. Only <see cref="ClipReadability.Unreadable"/> —
/// ffprobe running and rejecting the file — is grounds to fail an import.
///
/// The interpretation is split out and pure so the rule can be tested without a video file.
/// </remarks>
public static class ClipIntegrity
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Turns one ffprobe run into a verdict. Pure.</summary>
    /// <remarks>
    /// A non-zero exit is ffprobe saying it could not parse the file — "moov atom not found" is the
    /// case this was written for. Exit 0 with a positive duration is a clean pass. Exit 0 with no
    /// duration is genuinely ambiguous (some containers simply do not carry one), so it is Unknown
    /// rather than a failure: refusing an import over that would repeat the bug this class fixed.
    /// </remarks>
    public static ClipCheck Interpret(int exitCode, string? stdout, string? stderr)
    {
        if (exitCode != 0)
        {
            var reason = LastLine(stderr);
            return new ClipCheck(ClipReadability.Unreadable,
                reason.Length == 0 ? $"ffprobe exited with code {exitCode}" : reason);
        }

        var duration = DurationFrom(stdout);
        if (duration > TimeSpan.Zero)
            return new ClipCheck(ClipReadability.Readable, $"{duration.TotalSeconds:F2}s");

        return new ClipCheck(ClipReadability.Unknown, "ffprobe reported no duration");
    }

    /// <summary>
    /// Runs ffprobe against <paramref name="clipPath"/>. Never throws; anything that goes wrong
    /// with the check itself is <see cref="ClipReadability.Unknown"/>, never Unreadable.
    /// </summary>
    public static async Task<ClipCheck> VerifyAsync(
        string ffmpegPath, string clipPath, CancellationToken ct = default)
    {
        var probe = ThumbnailService.ProbePathFor(ffmpegPath);
        if (probe is null || !File.Exists(probe))
            return new ClipCheck(ClipReadability.Unknown, "ffprobe was not found next to ffmpeg");

        if (!File.Exists(clipPath))
            return new ClipCheck(ClipReadability.Unreadable, "the file was not written at all");

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
                         "-show_entries", "format=duration",
                         "-of", "default=noprint_wrappers=1:nokey=1",
                         clipPath
                     })
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new ClipCheck(ClipReadability.Unknown, "ffprobe could not be started");

            // Drained, not ignored: a full pipe buffer deadlocks the child.
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
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return new ClipCheck(ClipReadability.Unknown, "ffprobe timed out");
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

            return Interpret(process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return new ClipCheck(ClipReadability.Unknown, "the check was cancelled");
        }
        catch (Exception ex)
        {
            return new ClipCheck(ClipReadability.Unknown, $"the check failed: {ex.Message}");
        }
    }

    private static TimeSpan DurationFrom(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return TimeSpan.Zero;

        foreach (var line in stdout.Split('\n'))
        {
            // nokey output, so the line is the bare number. N/A parses as nothing, which is the
            // ambiguous case the caller treats as Unknown.
            if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                                out var seconds) && seconds > 0)
                return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.Zero;
    }

    private static string LastLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                     StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
