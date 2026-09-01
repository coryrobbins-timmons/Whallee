using System.Diagnostics;
using System.Globalization;

namespace Whallee.Core.Media;

/// <summary>Fetches title, duration, thumbnail and licence without downloading any media.</summary>
/// <remarks>
/// ONE --print FLAG PER FIELD, not the spec's single pipe-delimited template: YouTube titles
/// routinely contain '|', which would corrupt every field after the title. yt-dlp emits one line
/// per flag in order, and a title cannot contain a newline.
/// </remarks>
public static class YtDlpProbe
{
    /// <summary>Absent fields come back as this literal.</summary>
    private const string NotAvailable = "NA";

    public static IReadOnlyList<string> ArgumentsFor(string url) => new[]
    {
        "--no-warnings", "--no-playlist",
        "--print", "%(title)s",
        "--print", "%(duration)s",
        "--print", "%(thumbnail)s",
        "--print", "%(license)s",
        url
    };

    /// <summary>Parses the four lines yt-dlp prints. Null when the output is not usable.</summary>
    public static VideoMetadata? ParseLines(IReadOnlyList<string> lines)
    {
        if (lines.Count < 4) return null;

        var seconds = Field(lines[1]);
        if (seconds is null ||
            !double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
            return null;   // live streams report NA and have no section to import

        return new VideoMetadata(
            lines[0].Trim(),
            TimeSpan.FromSeconds(value),
            Field(lines[2]),
            Field(lines[3]));
    }

    private static string? Field(string raw)
    {
        var trimmed = raw.Trim();
        return trimmed.Length == 0 || trimmed == NotAvailable ? null : trimmed;
    }

    /// <summary>Runs the probe. Returns null when yt-dlp fails or prints nothing usable.</summary>
    public static async Task<VideoMetadata?> ProbeAsync(
        string ytDlpPath, string url, CancellationToken ct)
    {
        var info = new ProcessStartInfo(ytDlpPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in ArgumentsFor(url)) info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null) return null;

        // Start both reads concurrently. If we only read stdout, a large stderr (errors,
        // deprecation notices, tracebacks) can fill the OS pipe buffer, blocking the child
        // until it exits — but the child never exits because its stderr pipe is full and it's
        // blocked writing to it. This is a classic deadlock. Reading both concurrently prevents it.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0) return null;

        var stdout = await stdoutTask.ConfigureAwait(false);
        return ParseLines(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                              StringSplitOptions.TrimEntries));
    }
}
