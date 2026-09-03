using System.IO;
using Whallee.App.Services;
using Whallee.Core.Configuration;
using Whallee.Core.Displays;

namespace Whallee.App.ViewModels;

/// <summary>One display's row on the settings page.</summary>
public sealed class DisplayRow
{
    public required string DeviceName { get; init; }

    /// <summary>The clip playing right now, or null when this display has no active player.</summary>
    public string? NowPlaying { get; init; }

    /// <summary>What the user has chosen. Starts at the saved value and is edited in place.</summary>
    public DisplayMode Mode { get; set; }

    /// <summary>False when nothing is playing, so there is no clip to pin to.</summary>
    public bool CanPin => NowPlaying is not null;
}

/// <summary>
/// Backs the settings page: current values, pending choices, and applying them.
/// </summary>
public sealed class SettingsViewModel
{
    private readonly EngineHost _engine;
    private readonly UiReporter _reporter;

    public List<DisplayRow> Displays { get; } = new();

    /// <summary>The pending scaling choice. Starts at the saved value.</summary>
    public ScalingMode Scaling { get; set; }

    public SettingsViewModel(EngineHost engine, UiReporter reporter)
    {
        _engine = engine;
        _reporter = reporter;
        Refresh();
    }

    // ---- read-only "About" values -------------------------------------------

    public string LibraryFolder => Shown(_engine.Config.LibraryFolder);
    public string MpvPath => Shown(_engine.Config.MpvPath);
    public string FfmpegPath => Shown(_engine.Config.FfmpegPath);
    public string YtDlpPath => Shown(_engine.Config.YtDlpPath);
    public static string ConfigPath => ConfigStore.DefaultPath;
    public string LogPath => _reporter.LogPath ?? "(file logging is off)";

    private static string Shown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(not found)" : value;

    // ---- logon shortcut ------------------------------------------------------

    /// <summary>False until the app has been published to its install path.</summary>
    public static bool CanStartWithWindows => StartupShortcut.IsInstalled();

    /// <summary>Read from the shortcut on disk, which is the source of truth.</summary>
    public static bool StartsWithWindows => StartupShortcut.Exists();

    /// <summary>
    /// Creates or removes the logon shortcut. Returns false only when enabling was declined for
    /// want of an installed copy. Shell and file I/O - call this from a background thread.
    /// </summary>
    public bool ApplyStartupShortcut(bool enabled)
    {
        if (!enabled)
        {
            StartupShortcut.Remove();
            return true;
        }

        return StartupShortcut.Create();
    }

    /// <summary>
    /// Records the shortcut's ACTUAL state in config and saves.
    /// </summary>
    /// <remarks>
    /// SaveConfig, not ApplyConfigChange: nothing the running mpv processes depend on has changed,
    /// so restarting three players would be a visible flicker for nothing. Same split the rest of
    /// the app draws.
    ///
    /// Reads Exists() rather than trusting the requested value, so a Create() that declined - or a
    /// .lnk the user deleted by hand - is reflected instead of contradicted.
    /// </remarks>
    public void RecordStartWithWindows()
    {
        _engine.Config.StartWithWindows = StartupShortcut.Exists();
        _engine.SaveConfig();

        _reporter.Report(_engine.Config.StartWithWindows
            ? "Whallee will start with Windows"
            : "Whallee will not start with Windows");
    }

    public void ReportNotInstalled() => _reporter.Report(
        "cannot start with Windows yet — there is no installed copy at "
        + $"{StartupShortcut.InstalledExePath}. Run tools\\publish-app.ps1 first.");

    // ---- rows ----------------------------------------------------------------

    /// <summary>
    /// Rebuilds the rows from what is attached and playing right now.
    /// </summary>
    /// <remarks>
    /// Called every time the page is shown, for the same reason LibraryViewModel.RefreshTargets is:
    /// the page instance is cached by MainWindow, so without this its rows would stay frozen at
    /// whatever was attached when the window first opened.
    ///
    /// It also discards pending edits, which is correct - a pending "pin this" against a display
    /// that has since been unplugged is not worth preserving.
    /// </remarks>
    public void Refresh()
    {
        Scaling = _engine.Config.Scaling;

        var playing = _engine.ActivePlayers;

        Displays.Clear();
        foreach (var display in DisplayEnumerator.Enumerate())
        {
            var active = playing.FirstOrDefault(
                p => string.Equals(p.Display.DeviceName, display.DeviceName,
                                   StringComparison.OrdinalIgnoreCase));

            var saved = _engine.Config.Displays.FirstOrDefault(
                a => string.Equals(a.DeviceName, display.DeviceName,
                                   StringComparison.OrdinalIgnoreCase));

            Displays.Add(new DisplayRow
            {
                DeviceName = display.DeviceName,
                NowPlaying = active is null ? null : Path.GetFileName(active.ClipPath),
                Mode = saved?.Mode ?? DisplayMode.Shuffle
            });
        }
    }

    // ---- applying -------------------------------------------------------------

    public bool IsDirty => SettingsDraft.HasChanges(_engine.Config, Scaling, BuildChoices());

    /// <summary>
    /// Writes the pending changes and re-attaches. Returns how many fields changed.
    /// </summary>
    /// <remarks>
    /// ApplyConfigChange, not SaveConfig: scaling feeds mpv's command line and a pin decides which
    /// clip a display plays, so the players must be restarted for either to take effect. That
    /// restart runs inline on the caller's thread, which is correct here - Restart pumps messages
    /// throughout, so the desktop stays responsive - and it is exactly why these changes sit behind
    /// one button rather than applying as each control is clicked.
    /// </remarks>
    public int Apply()
    {
        var changed = SettingsDraft.Apply(_engine.Config, Scaling, BuildChoices());
        if (changed == 0) return 0;

        _reporter.Report($"applying {changed} setting change(s)");
        _engine.ApplyConfigChange();
        Refresh();

        return changed;
    }

    /// <summary>
    /// The pending choices, resolved against what is playing RIGHT NOW.
    /// </summary>
    /// <remarks>
    /// BUILT ON EVERY CALL, NEVER CACHED. "Pinned" in the UI means "pin whatever this display is
    /// playing", and the engine can restart underneath this page - a display change triggers one -
    /// so a dictionary built when the page loaded would pin the clip from before that restart.
    /// Both IsDirty and Apply go through here for that reason.
    /// </remarks>
    private Dictionary<string, DisplayChoice> BuildChoices()
    {
        var playing = _engine.ActivePlayers;
        var choices = new Dictionary<string, DisplayChoice>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Displays)
        {
            string? clip = null;

            if (row.Mode == DisplayMode.Pinned)
            {
                var active = playing.FirstOrDefault(
                    p => string.Equals(p.Display.DeviceName, row.DeviceName,
                                       StringComparison.OrdinalIgnoreCase));

                clip = active is null ? null : Path.GetFileName(active.ClipPath);
            }

            choices[row.DeviceName] = new DisplayChoice(row.Mode, clip);
        }

        return choices;
    }
}
