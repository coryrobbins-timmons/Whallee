namespace Whallee.Core.Configuration;

/// <summary>
/// The rules a genre name and the ordered genre list obey. Pure, so all of it is tested.
/// </summary>
/// <remarks>
/// SEPARATE FROM THE INDEX THAT STORES THEM, on the same reasoning as ClipRename: the naming rules
/// are what a dialog needs in order to say "no, and here is why" BEFORE anything is written, and a
/// dialog should not have to hold an index to ask.
/// </remarks>
public static class ClipGenres
{
    /// <summary>Longest genre name accepted. A tab wider than the window is unusable.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Trims a typed name. Whitespace is not an error, it is simply removed.</summary>
    public static string Normalise(string? name) => (name ?? string.Empty).Trim();

    /// <summary>
    /// Whether two genre names refer to the same genre.
    /// </summary>
    /// <remarks>
    /// THE ONLY PLACE THIS COMPARISON IS WRITTEN. Genres are matched case-insensitively everywhere -
    /// Windows users do not expect "Cars" and "cars" to differ - and a second, subtly different
    /// comparison elsewhere is how a clip ends up in a genre the bar cannot show.
    /// </remarks>
    public static bool Same(string? a, string? b) =>
        string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Why this genre name cannot be used, or null when it is fine.</summary>
    /// <param name="existing">Genres that already exist.</param>
    /// <param name="currentName">
    /// The name being replaced, when renaming. Excluded from the duplicate check so that recasing a
    /// genre to its own name - a real edit - is not rejected as a duplicate of itself.
    /// </param>
    public static string? Validate(
        string? name, IEnumerable<string> existing, string? currentName = null)
    {
        var candidate = Normalise(name);

        if (candidate.Length == 0) return "Enter a name.";

        if (candidate.Length > MaxNameLength)
            return $"That name is too long — keep it under {MaxNameLength} characters.";

        foreach (var other in existing)
        {
            if (currentName is not null && Same(other, currentName)) continue;
            if (Same(other, candidate)) return $"There is already a genre called {other}.";
        }

        return null;
    }

    /// <summary>
    /// Moves a genre within the ordered list. Returns its new index, or -1 if it is not there.
    /// </summary>
    /// <remarks>
    /// A MOVE PAST EITHER END IS A NO-OP, NOT AN ERROR. The context menu offers "Move left" on the
    /// first tab, so the caller would otherwise have to know the position to avoid a crash.
    /// </remarks>
    public static int Move(IList<string> genres, string name, int delta)
    {
        var from = -1;
        for (var i = 0; i < genres.Count; i++)
            if (Same(genres[i], name)) { from = i; break; }

        if (from < 0) return -1;

        var to = from + delta;
        if (to < 0 || to >= genres.Count) return from;

        var moved = genres[from];
        genres.RemoveAt(from);
        genres.Insert(to, moved);
        return to;
    }
}
