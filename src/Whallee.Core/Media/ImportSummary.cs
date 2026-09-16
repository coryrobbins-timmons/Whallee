namespace Whallee.Core.Media;

/// <summary>
/// What to tell the user after an import: one short headline, and a caveat only when reality
/// differed from what they asked for.
/// </summary>
public readonly record struct ImportSummaryText(string Headline, string? Detail);

/// <summary>
/// Composes the after-import wording.
/// </summary>
/// <remarks>
/// PURE, AND SEPARATE FROM THE PAGE, because this wording is load-bearing and easy to break by
/// accident. The URL dialog deliberately predicts nothing in advance — that was decided on
/// 2026-09-02 — which makes this the ONLY place a user ever learns that the network capped their
/// clip or ignored their start time. A refactor that quietly drops the caveat would take that
/// information out of the product without failing anything.
///
/// It replaced a stock MessageBox that ran to four lines of prose and, being system-themed, looked
/// nothing like the rest of the app. Short headline, caveat only when it applies.
/// </remarks>
public static class ImportSummary
{
    /// <param name="clipName">The clip's name without extension, as the library shows it.</param>
    /// <param name="requested">The section length the user asked for.</param>
    /// <param name="requestedStart">The start offset the user asked for.</param>
    /// <param name="actual">What was actually imported.</param>
    /// <param name="strategy">
    /// The importer's strategy letter. "C" is the prefix-only path: it can fetch the beginning of a
    /// video and nothing else, so a non-zero start was necessarily ignored.
    /// </param>
    /// <param name="staleToolAgeDays">
    /// How many days old the installed yt-dlp is, when it is past
    /// <see cref="YtDlpVersion.StaleAfterDays"/>; null when it is current or could not be read.
    /// </param>
    public static ImportSummaryText Describe(
        string clipName, TimeSpan requested, TimeSpan requestedStart,
        TimeSpan actual, string strategy, int? staleToolAgeDays = null)
    {
        // "— 5:00 long", NOT "— 5:00". A bare duration ends this sentence right after a wait that
        // can run to minutes, and on 2026-09-11 the owner read it as how long the DOWNLOAD took
        // rather than how long the CLIP is. One word removes the ambiguity.
        var headline = $"Imported {clipName} — {Clock(actual)} long.";

        var shorterThanAsked = actual < requested;
        var startIgnored = strategy == "C" && requestedStart > TimeSpan.Zero;

        // NO LONGER BLAMES "THIS NETWORK", and that correction matters. Every one of these
        // sentences used to open with it, on the belief that a ~60 s cap was imposed by the owner's
        // network. It was not: yt-dlp was stale and resolved videos through the android_vr client,
        // which YouTube itself serves about a minute of. A current yt-dlp reaches any timestamp at
        // full quality. Telling the owner their network is at fault would now send them to argue
        // with the wrong people, so these say only what is observable - what was asked for, and
        // what arrived.
        //
        // Both caveats in one sentence when both apply. Two sentences for two facts about the same
        // clip reads like two separate problems.
        var detail = (shorterThanAsked, startIgnored) switch
        {
            (true, true) =>
                $"Only {Clock(actual)} of the {Clock(requested)} you asked for could be fetched, "
                + $"and it begins at the beginning rather than {Clock(requestedStart)}.",
            (true, false) =>
                $"Only {Clock(actual)} of the {Clock(requested)} you asked for could be fetched.",
            (false, true) =>
                $"This clip begins at the beginning rather than {Clock(requestedStart)}.",
            _ => null
        };

        // APPENDED, NEVER FOLDED INTO THE SWITCH ABOVE. A third dimension turns four cases into
        // eight in the one method whose wording this file's own remarks call load-bearing and easy
        // to break by accident.
        //
        // ONLY WHEN THE CLIP CAME BACK SHORT. A stale yt-dlp that delivered what was asked for has
        // caused no harm, and a caveat on a good import teaches the owner to ignore this sentence -
        // which is the sentence the whole check exists to deliver.
        if (detail is not null && shorterThanAsked && staleToolAgeDays is { } days)
            detail += $" Your yt-dlp is {YtDlpVersion.DescribeAge(days)}, which is the most likely cause.";

        return new ImportSummaryText(headline, detail);
    }

    /// <summary>
    /// "54 seconds" under a minute, "5:00" at or above it.
    /// </summary>
    /// <remarks>
    /// Under a minute, m:ss makes the reader do arithmetic to answer "is that long?" — and almost
    /// every clip this network yields is under a minute, so that is the common case, not the edge.
    /// </remarks>
    private static string Clock(TimeSpan value)
    {
        var seconds = (int)Math.Round(value.TotalSeconds);
        if (seconds < 60) return seconds == 1 ? "1 second" : $"{seconds} seconds";

        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
