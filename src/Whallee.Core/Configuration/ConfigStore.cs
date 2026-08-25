using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whallee.Core.Configuration;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
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

    public static void Save(WhalleeConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }
}
