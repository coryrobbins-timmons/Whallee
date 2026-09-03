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

    /// <summary>Why this base name cannot be used, or null when it is fine.</summary>
    /// <remarks>
    /// A name identical to the clip's current one is NOT rejected here - this method does not know
    /// the old name. "Nothing to do" is the dialog's concern, and it disables its button rather
    /// than showing an error for opening a dialog and changing nothing.
    /// </remarks>
    public static string? Validate(string? baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
            return "Enter a name.";

        if (baseName.Length > MaxBaseNameLength)
            return $"That name is too long — keep it under {MaxBaseNameLength} characters.";

        if (baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A name cannot contain any of  \\ / : * ? \" < > |";

        // WINDOWS SILENTLY STRIPS A TRAILING DOT OR SPACE, so the file would end up with a
        // different name than the one typed - a mismatch the user cannot see, and cannot fix by
        // retyping the same thing.
        if (baseName != baseName.TrimEnd('.', ' '))
            return "A name cannot end with a dot or a space.";

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
