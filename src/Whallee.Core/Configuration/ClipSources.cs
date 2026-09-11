using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whallee.Core.Configuration;

/// <summary>Where a clip came from, recorded at import.</summary>
/// <param name="Url">The link the user pasted.</param>
/// <param name="Start">The start offset they asked for.</param>
/// <param name="Length">The section length they asked for.</param>
/// <param name="ImportedUtc">When it was imported.</param>
public sealed record ClipSource(string Url, TimeSpan Start, TimeSpan Length, DateTimeOffset ImportedUtc);

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

            var entries = JsonSerializer.Deserialize<Dictionary<string, ClipSource>>(
                File.ReadAllText(path), Options);

            return ClipSourceIndex.From(entries);
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
            File.WriteAllText(
                temp, JsonSerializer.Serialize(index.Entries, Options));

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
