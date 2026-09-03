namespace Whallee.Core.Configuration;

/// <summary>
/// Renaming a clip: which names are usable, and repointing the pins that named the old one.
/// </summary>
/// <remarks>
/// SIBLING OF ClipRemoval, and the same shape of problem. PinnedPath refers to a clip BY BARE FILE
/// NAME, so a rename silently breaks every pin that named it - and ClipResolver.Resolve then falls
/// back to a random clip ON PURPOSE, with nothing on screen to say why that display stopped
/// honouring the pin. That fallback is a live, exercised path, confirmed 2026-09-03 when deleting a
/// playing clip did exactly this.
///
/// PURE, so the half of a rename that can go wrong INVISIBLY is the half with coverage. The
/// File.Move itself fails loudly and needs no help.
/// </remarks>
public static class ClipRename
{
    /// <summary>Longest base name accepted, before the extension.</summary>
    /// <remarks>
    /// The library folder sits deep under OneDrive-redirected Documents - roughly 88 characters
    /// before the file name even begins - so a long name approaches MAX_PATH. Rejecting here is
    /// cheaper than explaining a PathTooLongException thrown from somewhere further down.
    /// </remarks>
    private const int MaxBaseNameLength = 120;

    /// <summary>The characters named in the invalid-character message, in the order shown.</summary>
    /// <remarks>
    /// A readable subset of <see cref="Path.GetInvalidFileNameChars"/>, which also contains every
    /// control character from 0 to 31 - listing those would be noise, since nobody types them on
    /// purpose. The CHECK still uses the full framework set; only the MESSAGE uses this list.
    /// </remarks>
    private const string CommonInvalidChars = "\\ / : * ? \" < > |";

    /// <summary>
    /// Trims what Windows would strip anyway: whitespace at either end, and trailing dots.
    /// </summary>
    /// <remarks>
    /// TRIMMED RATHER THAN REJECTED, changed 2026-09-03 on the owner's request. A trailing space is
    /// the easiest slip to make and the hardest to see, and Windows silently strips both it and a
    /// trailing dot when creating the file - so the choice was only ever between doing that
    /// silently ourselves or scolding the user for a mistake with one obvious fix. A trailing dot
    /// goes the same way for consistency: two near-identical slips behaving differently is worse
    /// than either rule on its own.
    ///
    /// LEADING whitespace goes too. It is legal on Windows, but it is invisible in a file listing
    /// and essentially never intended.
    ///
    /// TWO STEPS, NOT ONE. Trim() first, so "forest . " reaches TrimEnd as "forest ."; TrimEnd then
    /// removes the dot and the space it exposes in a single pass, because it accepts both.
    ///
    /// CALL THIS AT COMMIT, NEVER ON EVERY KEYSTROKE. Trimming as the user types would delete the
    /// space the instant it is typed, making a two-word name impossible to enter.
    /// </remarks>
    public static string Normalise(string? baseName) =>
        (baseName ?? string.Empty).Trim().TrimEnd('.', ' ');

    /// <summary>Why this base name cannot be used, or null when it is fine.</summary>
    /// <remarks>
    /// VALIDATES THE NORMALISED NAME, so a trailing space or dot is not an error - it is simply
    /// trimmed. See <see cref="Normalise"/>. A name that is nothing BUT whitespace and dots still
    /// fails, because normalising it leaves nothing to name the file.
    ///
    /// A name identical to the clip's current one is NOT rejected here - this method does not know
    /// the old name. "Nothing to do" is the dialog's concern, and it disables its button rather
    /// than showing an error for opening a dialog and changing nothing.
    /// </remarks>
    public static string? Validate(string? baseName)
    {
        var name = Normalise(baseName);

        if (name.Length == 0)
            return "Enter a name.";

        if (name.Length > MaxBaseNameLength)
            return $"That name is too long — keep it under {MaxBaseNameLength} characters.";

        // NAMES THE OFFENDING CHARACTER, not just the rule. "A name cannot contain \ / : * ? etc"
        // leaves the user comparing their name against a list; quoting the one they actually typed
        // points straight at it. Distinct() because pasting a path yields the same character
        // repeatedly and "\ \ \" reads as a stutter.
        var offenders = name.Where(c => Path.GetInvalidFileNameChars().Contains(c))
                            .Distinct()
                            .ToArray();

        if (offenders.Length > 0)
        {
            // Control characters have no printable form, so describe rather than quote them.
            var shown = string.Join(" ", offenders.Select(
                c => char.IsControl(c) ? "(a control character)" : c.ToString()));

            return $"A name cannot contain {shown} — these are not allowed: {CommonInvalidChars}";
        }

        return null;
    }

    /// <summary>
    /// Points every pin naming <paramref name="oldFileName"/> at <paramref name="newFileName"/>,
    /// and returns how many were repointed.
    /// </summary>
    /// <remarks>
    /// REPOINTS REGARDLESS OF Mode, AND NEVER TOUCHES Mode. A Shuffle display carrying a stale
    /// PinnedPath has that reference carried across too, so it keeps naming a file that exists -
    /// mirroring how ClipRemoval clears such a reference rather than leaving it dangling.
    ///
    /// Path.GetFileName is applied to the stored value because ClipResolver also accepts a rooted
    /// pin pointing inside the library, which would otherwise never match.
    /// </remarks>
    public static int Repoint(WhalleeConfig config, string oldFileName, string newFileName)
    {
        var repointed = 0;

        foreach (var assignment in config.Displays)
        {
            if (string.IsNullOrWhiteSpace(assignment.PinnedPath)) continue;

            if (!string.Equals(Path.GetFileName(assignment.PinnedPath), oldFileName,
                               StringComparison.OrdinalIgnoreCase))
                continue;

            assignment.PinnedPath = newFileName;
            repointed++;
        }

        return repointed;
    }
}
