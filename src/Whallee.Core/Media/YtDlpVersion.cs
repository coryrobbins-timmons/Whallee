using System.Diagnostics;
using System.Globalization;

namespace Whallee.Core.Media;

/// <summary>
/// How old the installed yt-dlp is, and what to say about it.
/// </summary>
/// <remarks>
/// YT-DLP'S VERSION IS ITS RELEASE DATE - "2026.08.19" - so staleness is subtraction against the
/// system clock. There is no network call anywhere in this class, nothing to authenticate, no rate
/// limit, and nothing that behaves differently offline. Do not "improve" this by asking GitHub what
/// the latest release is; the version already answers the question.
///
/// This exists because of what happened on 2026-09-14. A two-month-stale yt-dlp resolved every
/// video through YouTube's android_vr client, which googlevideo serves about a minute of before
/// answering 403. That produced a "~60 second network limit" which was measured five times, written
/// into CLAUDE.md with a bitrate table, and repeated in capitals across three files. None of it was
/// true of the network. Updating yt-dlp fixed imports with zero code changes. yt-dlp will go stale
/// again - this class is what makes the next one a sentence on screen instead of a week.
/// </remarks>
public static class YtDlpVersion
{
    /// <summary>
    /// yt-dlp releases roughly monthly, so 30 days would warn nearly every month including when
    /// nothing is wrong, and a warning seen constantly stops being read. 60 is where this install
    /// actually broke - accurate, but by then an import may already have been lost. 45 sits past a
    /// normal release gap and comfortably before the observed failure point.
    /// </summary>
    public const int StaleAfterDays = 45;

    private const string WingetCommand = "winget upgrade yt-dlp";
    private const string SelfUpdateCommand = "yt-dlp -U";

    /// <summary>Reads the release date out of "--version" output. Null when it is unreadable.</summary>
    public static DateOnly? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                      StringSplitOptions.TrimEntries).FirstOrDefault();
        var token = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null) return null;

        // THREE OR MORE, NOT EXACTLY THREE. Nightly and master builds append a fourth component
        // (2026.08.19.232709); the date is still the first three and those users need this too.
        var parts = token.Split('.');
        if (parts.Length < 3) return null;

        if (!Number(parts[0], out var year) ||
            !Number(parts[1], out var month) ||
            !Number(parts[2], out var day))
            return null;

        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool Number(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static int Age(DateOnly released, DateOnly today) => today.DayNumber - released.DayNumber;

    /// <summary>
    /// A version dated in the future - clock skew, or a nightly built elsewhere - is never stale.
    /// Age goes negative there, which this comparison already handles.
    /// </summary>
    public static bool IsStale(DateOnly released, DateOnly today) =>
        Age(released, today) >= StaleAfterDays;

    /// <summary>"11 days old", "6 weeks old", "3 months old".</summary>
    public static string DescribeAge(int days)
    {
        if (days < 14) return days == 1 ? "1 day old" : $"{days} days old";
        if (days < 60) return $"{days / 7} weeks old";
        return $"{days / 30} months old";
    }

    /// <summary>
    /// The command that updates THIS copy of yt-dlp.
    /// </summary>
    /// <remarks>
    /// INFERRED FROM THE PATH, not from which YtDlpLocator branch found it. That needs no change to
    /// the locator's signature or its tests, and it stays correct in the case a CONFIGURED path
    /// points into the winget tree - where a branch-based answer would say "yt-dlp -U" and be wrong.
    /// </remarks>
    public static string UpdateCommandFor(string ytDlpPath, string wingetPackagesRoot)
    {
        if (string.IsNullOrWhiteSpace(ytDlpPath) || string.IsNullOrWhiteSpace(wingetPackagesRoot))
            return SelfUpdateCommand;

        string full, root;
        try
        {
            full = Path.GetFullPath(ytDlpPath);
            root = Path.GetFullPath(wingetPackagesRoot).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception)
        {
            return SelfUpdateCommand;   // an unusable path is not a reason to name the wrong command
        }

        // THE SEPARATOR IS PART OF THE PREFIX. Without it "...\Packages" would also match
        // "...\PackagesExtra\yt-dlp.exe", which is not inside the winget tree at all.
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? WingetCommand
            : SelfUpdateCommand;
    }

    /// <summary>
    /// The line the import dialog shows, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// THIS IS A DELIBERATE EXCEPTION TO UrlImportDialog'S NO-PREDICTION RULE, and the reasoning
    /// belongs with the words. That rule came from removing a ~60s estimate on 2026-09-02, because
    /// knowing the ceiling in advance COULD NOT CHANGE WHAT THE USER DOES. This can: a stale tool is
    /// a local, checkable fact, and the owner's response to reading it is to go and update. See the
    /// matching note in UrlImportDialog before deleting this as a rule violation.
    /// </remarks>
    public static string? DialogWarning(
        DateOnly? released, DateOnly today, string ytDlpPath, string wingetPackagesRoot)
    {
        if (released is not { } date || !IsStale(date, today)) return null;

        return $"yt-dlp is {DescribeAge(Age(date, today))}. Imports may come back short or fail — "
             + $"update with: {UpdateCommandFor(ytDlpPath, wingetPackagesRoot)}";
    }
}
