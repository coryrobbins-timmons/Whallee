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
    public static ImportSummaryText Describe(
        string clipName, TimeSpan requested, TimeSpan requestedStart,
        TimeSpan actual, string strategy)
    {
        var headline = $"Imported {clipName} — {Clock(actual)}.";

        var shorterThanAsked = actual < requested;
        var startIgnored = strategy == "C" && requestedStart > TimeSpan.Zero;

        // Both caveats in one sentence when both apply. Two sentences for two facts about the same
        // clip reads like two separate problems.
        var detail = (shorterThanAsked, startIgnored) switch
        {
            (true, true) =>
                $"This network capped it at {Clock(actual)} of the {Clock(requested)} you asked for, "
                + $"and refused to start at {Clock(requestedStart)} — it begins at the beginning.",
            (true, false) =>
                $"This network capped it at {Clock(actual)} of the {Clock(requested)} you asked for.",
            (false, true) =>
                $"This network refused to start at {Clock(requestedStart)} — it begins at the beginning.",
            _ => null
        };

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
