using System.Diagnostics;

namespace Whallee.Core.Media;

/// <summary>
/// Runs the update the stale-tool warning names, so the owner can click instead of typing it.
/// </summary>
/// <remarks>
/// WHICH COMMAND IS DECIDED BY YtDlpVersion.UpdateCommandFor, not re-derived here. The warning text
/// and the button must never disagree about what "update" means for this copy of yt-dlp.
///
/// SUCCESS IS JUDGED BY THE CALLER RE-READING THE VERSION, NOT BY THE EXIT CODE. winget returns
/// distinct non-zero codes for "nothing to upgrade" and "upgraded, restart needed", and yt-dlp -U's
/// codes are not documented as stable. The version on disk afterwards is the only fact that matters.
/// </remarks>
public static class YtDlpUpdater
{
    /// <summary>The winget package id. --exact so "yt-dlp" cannot match a lookalike package.</summary>
    private const string WingetPackageId = "yt-dlp.yt-dlp";

    /// <summary>The executable and arguments that update the copy of yt-dlp at this path.</summary>
    public static (string FileName, IReadOnlyList<string> Arguments) CommandFor(
        string ytDlpPath, string wingetPackagesRoot)
    {
        var isWinget = YtDlpVersion.UpdateCommandFor(ytDlpPath, wingetPackagesRoot)
                                   .StartsWith("winget", StringComparison.OrdinalIgnoreCase);

        // NON-INTERACTIVE FLAGS ARE REQUIRED, not tidiness. There is no console window to answer a
        // source-agreement prompt in, so without them winget waits on stdin until the timeout.
        return isWinget
            ? ("winget", new[]
              {
                  "upgrade", "--id", WingetPackageId, "--exact", "--silent",
                  "--accept-source-agreements", "--accept-package-agreements",
                  "--disable-interactivity"
              })
            : (ytDlpPath, new[] { "-U" });
    }

    /// <summary>The last non-empty line of the output - what the tool itself said happened.</summary>
    public static string? LastLine(string? stdout, string? stderr)
    {
        static string? Last(string? text) => text?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Length > 0);

        // stderr first: both tools report refusals ("use pip to update") and errors there.
        return Last(stderr) ?? Last(stdout);
    }

    /// <summary>
    /// Runs the update. Returns the tool's last line of output, or the exception message.
    /// </summary>
    /// <remarks>
    /// SAME PROCESS HYGIENE AS YtDlpVersion.ReadAsync, for the same reasons: both pipes drained
    /// concurrently, the tree killed in a finally, the wait bounded. The bound is minutes, not
    /// seconds - this one downloads.
    /// </remarks>
    public static async Task<string?> RunAsync(
        string ytDlpPath, string wingetPackagesRoot, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;

        var (fileName, arguments) = CommandFor(ytDlpPath, wingetPackagesRoot);
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return $"Could not start {fileName}.";

            // Closed at once so anything that does prompt reads end-of-input and gives up, rather
            // than hanging until the timeout.
            process.StandardInput.Close();

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
                var stderrTask = process.StandardError.ReadToEndAsync(token);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                await process.WaitForExitAsync(token).ConfigureAwait(false);

                return LastLine(await stdoutTask.ConfigureAwait(false),
                                await stderrTask.ConfigureAwait(false));
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The update took too long and was stopped.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
