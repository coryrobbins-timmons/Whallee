using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whallee.Core.Configuration;

/// <summary>Where a clip came from, recorded at import.</summary>
/// <param name="Url">The link the user pasted.</param>
/// <param name="Start">The start offset they asked for.</param>
/// <param name="Length">The section length they asked for.</param>
/// <param name="ImportedUtc">When it was imported.</param>
/// <param name="Genre">The genre this clip is filed under, or null for ungrouped.</param>
/// <remarks>
/// GENRE HAS A DEFAULT AND SITS LAST so every existing construction site keeps compiling. This
/// record is built positionally in ClipDetailsDialog and LibraryPage - do not reorder its members.
/// </remarks>
public sealed record ClipSource(
    string Url, TimeSpan Start, TimeSpan Length, DateTimeOffset ImportedUtc, string? Genre = null);

/// <summary>
/// Remembers which link each library clip came from.
/// </summary>
/// <remarks>
/// KEYED BY FILE NAME, NOT BY PATH, for the same reason DisplayAssignment.PinnedPath is: the
/// library folder is configurable and its clips are addressed relative to it everywhere else. An
/// absolute path stored here would break the moment the library moved.
///
/// CASE-INSENSITIVE, because Windows file names are. "City Skyline.mp4" and "city skyline.mp4" are
/// the same clip, and a case-sensitive map would quietly hold two entries for it.
///
/// THE INDEX IS NOT THE TRUTH; the library folder is. An entry whose file no longer exists is
/// harmless - nothing reads it - and a clip with no entry is normal, not an error: every clip
/// imported before 2026-09-11, and every clip added from a local file, has no link to remember.
/// Callers must treat a miss as "not recorded", never as a failure.
/// </remarks>
public sealed class ClipSourceIndex
{
    private readonly Dictionary<string, ClipSource> _byFileName;

    public ClipSourceIndex() : this(new Dictionary<string, ClipSource>()) { }

    private ClipSourceIndex(Dictionary<string, ClipSource> entries)
    {
        _byFileName = new Dictionary<string, ClipSource>(entries, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every entry, for serialisation. Not ordered.</summary>
    public IReadOnlyDictionary<string, ClipSource> Entries => _byFileName;

    /// <summary>Where <paramref name="fileName"/> came from, or null if it was never recorded.</summary>
    public ClipSource? Get(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null
            : _byFileName.TryGetValue(fileName, out var source) ? source : null;

    /// <summary>Records where a clip came from, replacing any previous entry for that name.</summary>
    /// <remarks>
    /// REPLACES, because a library file name is unique and re-importing to the same name means the
    /// clip genuinely came from somewhere new. Keeping the old entry would make Details lie.
    /// </remarks>
    public void Record(string fileName, ClipSource source)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        _byFileName[fileName] = source;
    }

    /// <summary>Follows a rename. True if there was an entry to move.</summary>
    /// <remarks>
    /// WITHOUT THIS, RENAMING A CLIP LOSES ITS LINK, silently and permanently - the same class of
    /// bug as a rename orphaning a pin, which ClipRename.Repoint exists to prevent.
    /// </remarks>
    public bool Rename(string? oldFileName, string? newFileName)
    {
        if (string.IsNullOrWhiteSpace(oldFileName) || string.IsNullOrWhiteSpace(newFileName))
            return false;
        if (!_byFileName.Remove(oldFileName, out var source)) return false;

        _byFileName[newFileName] = source;
        return true;
    }

    /// <summary>Forgets a clip. True if there was an entry to forget.</summary>
    public bool Remove(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && _byFileName.Remove(fileName);

    /// <summary>Rebuilds an index from stored entries. Null and blank keys are dropped.</summary>
    public static ClipSourceIndex From(IDictionary<string, ClipSource>? entries)
    {
        var index = new ClipSourceIndex();
        if (entries is null) return index;

        foreach (var (key, value) in entries)
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
                index.Record(key, value);

        return index;
    }

    /// <summary>
    /// The genres, in the order the owner put them in.
    /// </summary>
    /// <remarks>
    /// STORED, NOT DERIVED FROM THE CLIPS. Two reasons, and both are load-bearing: the ORDER is the
    /// owner's and cannot be recovered from the assignments, and a genre must survive having no
    /// clips in it - otherwise creating one, or moving its last clip elsewhere, makes the tab
    /// vanish mid-use.
    /// </remarks>
    private readonly List<string> _genres = new();

    public IReadOnlyList<string> Genres => _genres;

    /// <summary>The genre <paramref name="fileName"/> is filed under, or null for ungrouped.</summary>
    public string? GenreOf(string? fileName) => Get(fileName)?.Genre;

    /// <summary>
    /// Files a clip under a genre, or under none when <paramref name="genre"/> is null.
    /// </summary>
    /// <remarks>
    /// CREATES THE ENTRY IF THERE IS NONE. Most of the library was added from local files and has no
    /// recorded link at all; requiring a source before a clip could be tagged would leave exactly
    /// those clips untaggable.
    /// </remarks>
    public void Assign(string fileName, string? genre)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var name = genre is null ? null : ClipGenres.Normalise(genre);
        if (name is { Length: 0 }) name = null;

        if (name is not null && !_genres.Any(g => ClipGenres.Same(g, name)))
            _genres.Add(name);

        // A clip with no recorded source still needs somewhere to hold its genre. The empty URL is
        // what ClipDetailsDialog already shows as "Not recorded".
        var existing = Get(fileName)
            ?? new ClipSource(string.Empty, TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.Now);

        _byFileName[fileName] = existing with { Genre = name };
    }

    /// <summary>Every clip filed under <paramref name="genre"/>; null lists the ungrouped ones.</summary>
    public IReadOnlyList<string> FileNamesIn(string? genre) =>
        _byFileName
            .Where(pair => genre is null
                ? pair.Value.Genre is null
                : ClipGenres.Same(pair.Value.Genre, genre))
            .Select(pair => pair.Key)
            .ToList();

    /// <summary>
    /// Renames a genre and retags every clip in it. True if there was one to rename.
    /// </summary>
    /// <remarks>
    /// RETAGS THE CLIPS, not just the list entry. Renaming only the entry would leave every clip
    /// pointing at a genre no tab shows - they would silently become ungrouped.
    /// </remarks>
    public bool RenameGenre(string oldName, string newName)
    {
        var at = -1;
        for (var i = 0; i < _genres.Count; i++)
            if (ClipGenres.Same(_genres[i], oldName)) { at = i; break; }

        if (at < 0) return false;

        var name = ClipGenres.Normalise(newName);
        if (name.Length == 0) return false;

        // Replaced IN PLACE so the tab keeps its position - the owner asked to rename it, not to
        // move it.
        _genres[at] = name;

        foreach (var fileName in FileNamesIn(oldName))
            _byFileName[fileName] = _byFileName[fileName] with { Genre = name };

        return true;
    }

    /// <summary>
    /// Removes a genre and unassigns its clips. True if there was one to remove.
    /// </summary>
    /// <remarks>
    /// IT NEVER TOUCHES A FILE. Deleting a genre unfiles clips; the videos stay in the library and
    /// their entries stay in this index, keeping their source links. The confirmation the caller
    /// shows must say so.
    /// </remarks>
    public bool DeleteGenre(string name)
    {
        var at = -1;
        for (var i = 0; i < _genres.Count; i++)
            if (ClipGenres.Same(_genres[i], name)) { at = i; break; }

        if (at < 0) return false;

        foreach (var fileName in FileNamesIn(name))
            _byFileName[fileName] = _byFileName[fileName] with { Genre = null };

        _genres.RemoveAt(at);
        return true;
    }

    /// <summary>Moves a genre in the bar. Returns its new index, or -1 if it is not there.</summary>
    public int MoveGenre(string name, int delta) => ClipGenres.Move(_genres, name, delta);

    /// <summary>Rebuilds an index from stored clips and an ordered genre list.</summary>
    public static ClipSourceIndex From(
        IDictionary<string, ClipSource>? entries, IEnumerable<string>? genres)
    {
        var index = From(entries);

        if (genres is not null)
            foreach (var genre in genres)
            {
                var name = ClipGenres.Normalise(genre);
                if (name.Length > 0 && !index._genres.Any(g => ClipGenres.Same(g, name)))
                    index._genres.Add(name);
            }

        // A genre referenced by a clip but missing from the list would have no tab, so the clip
        // would be invisible under every filter. Belt and braces against a hand-edited file.
        foreach (var source in index._byFileName.Values)
        {
            if (source.Genre is not { Length: > 0 } genre) continue;
            if (!index._genres.Any(g => ClipGenres.Same(g, genre))) index._genres.Add(genre);
        }

        return index;
    }
}

/// <summary>The on-disk shape. Separate from the index so the index is not a DTO.</summary>
internal sealed class ClipSourceFile
{
    public Dictionary<string, ClipSource> Clips { get; set; } = new();
    public List<string> Genres { get; set; } = new();
}

/// <summary>Reads and writes <see cref="ClipSourceIndex"/> as JSON. Never throws on read.</summary>
/// <remarks>
/// KEPT BESIDE config.json RATHER THAN IN THE LIBRARY FOLDER. The owner browses the library in
/// Explorer - "Open file location" is a menu item - and a bookkeeping file sitting among their
/// videos is clutter they did not ask for. The trade-off is that moving the library to another
/// machine leaves the links behind, which costs a Details row and nothing else.
/// </remarks>
public static class ClipSourceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whallee", "sources.json");

    /// <summary>
    /// Loads the index, or an empty one if it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// NEVER THROWS, and that is deliberate rather than lazy. This is a convenience record: a
    /// corrupt or absent file must cost the owner one row in a details dialog, never the ability to
    /// open their library.
    /// </remarks>
    public static ClipSourceIndex Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ClipSourceIndex();

            var json = File.ReadAllText(path);

            // TWO FORMATS, DELIBERATELY. Before genres existed this file was a BARE MAP of file name
            // to ClipSource, and the owner already has recorded links in that shape. Reading only
            // the new format would discard them silently, which would look exactly like a bug in
            // the Details dialog. The discriminator is the "clips" property: the old format cannot
            // have one, because its keys are file names and a file name has an extension.
            using var document = JsonDocument.Parse(json);
            // TryGetProperty is case-sensitive, but Options serialises this as "Clips" (PascalCase);
            // match case-insensitively so the new format is recognised regardless of casing.
            var isNewFormat = document.RootElement.ValueKind == JsonValueKind.Object
                              && document.RootElement.EnumerateObject()
                                  .Any(p => string.Equals(p.Name, "clips", StringComparison.OrdinalIgnoreCase));

            if (!isNewFormat)
            {
                var legacy = JsonSerializer.Deserialize<Dictionary<string, ClipSource>>(json, Options);
                return ClipSourceIndex.From(legacy);
            }

            var file = JsonSerializer.Deserialize<ClipSourceFile>(json, Options);
            return ClipSourceIndex.From(file?.Clips, file?.Genres);
        }
        catch (Exception)
        {
            return new ClipSourceIndex();
        }
    }

    /// <summary>Writes the index. Same temp-then-replace shape as ConfigStore.</summary>
    public static void Save(ClipSourceIndex index, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(
                new ClipSourceFile
                {
                    Clips = new Dictionary<string, ClipSource>(index.Entries),
                    Genres = index.Genres.ToList()
                },
                Options));

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Best effort throughout: losing this file costs a Details row. It must never take an
            // import or a rename down with it, so the caller is not given anything to handle.
            try { File.Delete(temp); } catch (Exception) { /* nothing useful to do here */ }
        }
    }
}
