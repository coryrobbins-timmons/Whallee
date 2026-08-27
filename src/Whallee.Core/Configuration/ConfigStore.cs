using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whallee.Core.Configuration;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // Hand-editing config.json is a documented verification step, so a human typo in the case
        // of a key must not be silent. Without this, "pinnedpath" binds to nothing, the assignment
        // stays Shuffle, ClipResolver picks at random, and the result is indistinguishable from
        // "pinning is broken".
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whallee", "config.json");

    public static WhalleeConfig Load(string path)
    {
        if (!File.Exists(path))
            return new WhalleeConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WhalleeConfig>(json, Options) ?? new WhalleeConfig();
        }
        catch (JsonException)
        {
            // Keep the broken file so the user can recover hand-edits, then start clean.
            var backup = path + ".bak";
            File.Copy(path, backup, overwrite: true);
            return new WhalleeConfig();
        }
    }

    /// <summary>Writes the config, atomically from any reader's point of view.</summary>
    /// <remarks>
    /// Write-to-temp-then-move, not a direct overwrite. A crash or a lost power rail partway
    /// through a direct WriteAllText produces exactly the truncated file <see cref="Load"/> then
    /// has to recover from — and the user loses every pinned clip in it. File.Move with
    /// overwrite:true replaces the file in one filesystem operation, so a reader sees either the
    /// old config or the new one, never half of either. The temp file is in the same directory
    /// deliberately: a move across volumes degrades to copy-then-delete and is no longer atomic.
    /// </remarks>
    public static void Save(WhalleeConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
        File.Move(temp, path, overwrite: true);
    }
}
