using Whallee.Core.Media;

namespace Whallee.App;

/// <summary>
/// How old yt-dlp is, read once per app session.
/// </summary>
/// <remarks>
/// ONCE PER SESSION IS A REQUIREMENT, NOT AN OPTIMISATION. Import latency here is dominated by
/// process launches - yt-dlp is already launched twice for overlapping information, which is an
/// open follow-up in CLAUDE.md - so a --version call per import would worsen the very problem the
/// next piece of work is meant to fix.
///
/// UI THREAD ONLY. Every caller is a WPF handler on the dispatcher, so the field needs no lock.
/// Nothing here blocks: the task is started and awaited, never waited on. That matters more in this
/// codebase than in most - a thread that owns host windows and stops pumping freezes the whole
/// desktop, which has already happened four times from four different blocking calls.
/// </remarks>
internal static class YtDlpHealth
{
    private static string? _path;
    private static Task<DateOnly?>? _check;

    /// <summary>Starts the check on first call, then hands back the same task all session.</summary>
    public static Task<DateOnly?> VersionAsync(string ytDlpPath)
    {
        // Re-checks if the owner points Whallee at a different yt-dlp mid-session, which the
        // settings page allows. Same path, same answer, no second launch.
        if (_check is null || !string.Equals(_path, ytDlpPath, StringComparison.OrdinalIgnoreCase))
        {
            _path = ytDlpPath;
            _check = YtDlpVersion.ReadAsync(ytDlpPath);
        }

        return _check;
    }

    /// <summary>
    /// The cached age in days when yt-dlp is known to be stale; null otherwise.
    /// </summary>
    /// <remarks>
    /// READS THE CACHE WITHOUT AWAITING, deliberately. This is called on the import path, which must
    /// not wait on anything. By the time an import finishes, the dialog that started the check has
    /// been open for at least as long as it took the owner to paste a URL and look it up, so it has
    /// long since completed. If it somehow has not, the answer is null and the summary says nothing
    /// about the tool - which is the correct failure everywhere in this feature.
    /// </remarks>
    public static int? StaleAgeInDays()
    {
        if (_check is not { IsCompletedSuccessfully: true } completed) return null;
        if (completed.Result is not { } released) return null;

        var today = DateOnly.FromDateTime(DateTime.Now);
        return YtDlpVersion.IsStale(released, today) ? YtDlpVersion.Age(released, today) : null;
    }
}
