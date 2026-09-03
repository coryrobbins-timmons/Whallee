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
    /// has to recover from - and the user loses every pinned clip in it. File.Move with
    /// overwrite:true replaces the file in one filesystem operation, so a reader sees either the
    /// old config or the new one, never half of either. The temp file is in the same directory
    /// deliberately: a move across volumes degrades to copy-then-delete and is no longer atomic.
    /// A replace that is momentarily refused is waited out; see <see cref="ReplaceRetryDelaysMs"/>.
    /// Whether it succeeds or fails, this method leaves no temp file behind.
    /// </remarks>
    public static void Save(WhalleeConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            Replace(temp, path);
        }
        catch
        {
            // A save that fails leaves the previous config in place, so its temp file is pure
            // litter - and litter that the next Save would silently overwrite, hiding the
            // failure. Best effort only: the original exception is what the caller must act on.
            try { File.Delete(temp); } catch { /* nothing useful to do about it here */ }
            throw;
        }
    }

    /// <remarks>
    /// Replacing an existing file requires it to be free of every other handle: MoveFileEx has to
    /// delete the destination to put the temp file in its place. An on-access virus scanner opens
    /// config.json for a few milliseconds right after a save renames it into place, so a save that
    /// follows close behind another one lands inside that window and is refused with
    /// ERROR_ACCESS_DENIED. Nothing is wrong with the config or the disk; the file is simply busy.
    ///
    /// Measured on this machine (SentinelOne) over 40,000 back-to-back save pairs: 15 refusals
    /// while idle, 132 with a build running alongside - the scanner is busier, which is why this
    /// surfaced under `dotnet build` + `dotnet test` and almost never under `dotnet test` alone.
    /// Every single refusal cleared on the very next attempt, the longest hold being 19.5 ms.
    /// These delays sum to 155 ms, eight times that longest hold.
    ///
    /// The wait is bounded and it hides nothing: when the budget runs out the exception is
    /// rethrown, and <see cref="Save"/> removes the temp file on its way out either way.
    ///
    /// Callers on a thread that owns host windows - EngineHost.Start's second save is one - are
    /// buying up to 155 ms more of the synchronous-I/O exposure already accepted there, and only
    /// on the roughly 1-in-300 write that hits the window. The common path waits for nothing.
    /// </remarks>
    private static readonly int[] ReplaceRetryDelaysMs = { 5, 10, 20, 40, 80 };

    private static void Replace(string temp, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsBusy(ex) && attempt < ReplaceRetryDelaysMs.Length)
            {
                Thread.Sleep(ReplaceRetryDelaysMs[attempt]);
            }
        }
    }

    // A foreign handle on the destination surfaces as ERROR_ACCESS_DENIED, which .NET maps to
    // UnauthorizedAccessException - NOT an IOException, so catching IOException alone would miss
    // the case this exists for. Sharing and lock violations reach the same place by another route.
    private static bool IsBusy(Exception ex) =>
        ex is UnauthorizedAccessException ||
        (ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33);
}
