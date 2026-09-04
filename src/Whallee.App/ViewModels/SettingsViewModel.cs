using System.IO;
using Whallee.App.Services;
using Whallee.Core;
using Whallee.Core.Configuration;
using Whallee.Core.Displays;

namespace Whallee.App.ViewModels;

/// <summary>One display's row on the settings page.</summary>
public sealed class DisplayRow
{
    public required string DeviceName { get; init; }

    /// <summary>
    /// The clip playing right now, or null when this display has no active player. INFORMATION
    /// ONLY - nothing the user can do on this page depends on it. See <see cref="SelectedClip"/>.
    /// </summary>
    public string? NowPlaying { get; init; }

    /// <summary>What the user has chosen. Starts at the saved value and is edited in place.</summary>
    public DisplayMode Mode { get; set; }

    /// <summary>
    /// The clip this display would be pinned to, chosen from the library. Null only when the
    /// library is empty.
    /// </summary>
    /// <remarks>
    /// REPLACED A CanPin FLAG DERIVED FROM NowPlaying, WHICH WAS A BUG. Deciding whether a display
    /// could be pinned from WallpaperEngine.ActivePlayers coupled a config edit to transient
    /// playback state: ActivePlayers is EMPTY for the whole of a restart (StopAll clears it before
    /// StartOnce refills it), and a restart pumps messages, so the UI is live and interactive
    /// during that window. A fullscreen app changing display mode fires WM_DISPLAYCHANGE, the
    /// shell watcher restarts the engine, and a row built in that window came up "(not playing)"
    /// with Pinned disabled until the user navigated away and back. Owner-reported 2026-09-03.
    ///
    /// Pinning is a config edit and now reads only from config and the library, so no engine state
    /// can disable it.
    /// </remarks>
    public string? SelectedClip { get; set; }
}

/// <summary>
/// Backs the settings page: current values, pending choices, and applying them.
/// </summary>
public sealed class SettingsViewModel
{
    private readonly EngineHost _engine;
    private readonly UiReporter _reporter;

    public List<DisplayRow> Displays { get; } = new();

    /// <summary>
    /// Bare file names of every clip in the library, for the per-display pin dropdown. Empty until
    /// <see cref="LoadClipsAsync"/> has run, which is why rows seed their pin from config instead.
    /// </summary>
    public List<string> LibraryClips { get; } = new();

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

    // ---- performance rules ---------------------------------------------------

    public bool PauseOnFullscreen => _engine.Config.PauseOnFullscreen;
    public bool PauseOnBattery => _engine.Config.PauseOnBattery;

    /// <summary>Records both Performance rules and saves.</summary>
    /// <remarks>
    /// SaveConfig, NOT ApplyConfigChange. Neither rule changes any player's command line, so
    /// restarting three mpv processes would be a visible flicker for nothing - the same split the
    /// rest of this class draws. PlaybackGovernor reads config on its next tick, so a change takes
    /// effect within a second without anything restarting.
    ///
    /// BOTH VALUES ARE WRITTEN TOGETHER because the page hands over the state of both checkboxes;
    /// that keeps config and the UI in step even if a handler is ever missed.
    /// </remarks>
    public void SetPauseRules(bool onFullscreen, bool onBattery)
    {
        _engine.Config.PauseOnFullscreen = onFullscreen;
        _engine.Config.PauseOnBattery = onBattery;
        _engine.SaveConfig();

        _reporter.Report(
            $"pause rules — fullscreen: {(onFullscreen ? "on" : "off")}, " +
            $"battery: {(onBattery ? "on" : "off")}");
    }

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

            var nowPlaying = active is null ? null : Path.GetFileName(active.ClipPath);

            Displays.Add(new DisplayRow
            {
                DeviceName = display.DeviceName,
                NowPlaying = nowPlaying,
                Mode = saved?.Mode ?? DisplayMode.Shuffle,

                // Seeded from config first, so a row is usable the instant it appears - before
                // LoadClipsAsync has finished listing the library, and regardless of whether the
                // engine happens to be mid-restart with no active players.
                SelectedClip = SettingsDraft.InitialPin(saved?.PinnedPath, nowPlaying)
            });
        }
    }

    /// <summary>
    /// Fills <see cref="LibraryClips"/>, and gives any row with no pin yet something to point at.
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM Refresh, AND ASYNC, because listing the library is real disk I/O on a
    /// OneDrive-backed folder - exactly the call LibraryViewModel.LoadAsync pushes off the UI
    /// thread for the same reason. Refresh stays synchronous so rows appear at once; the dropdowns
    /// fill in a moment later, the same way the details dialog fills in its probed fields.
    ///
    /// Rows already carry a SelectedClip from config by the time this runs, so a slow listing
    /// delays the CHOICES, never the ability to pin.
    /// </remarks>
    public async Task LoadClipsAsync(CancellationToken ct = default)
    {
        var folder = _engine.Config.LibraryFolder;

        IReadOnlyList<string> files;
        try
        {
            files = await Task.Run(() => ClipResolver.ListLibraryFiles(folder), ct)
                              .ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _reporter.Report($"could not list the library folder: {ex.Message}");
            return;
        }

        LibraryClips.Clear();
        foreach (var file in files) LibraryClips.Add(Path.GetFileName(file));

        // A display that has never been pinned and is not playing anything would otherwise have
        // nothing selected, and choosing Pinned would then silently do nothing at Apply.
        foreach (var row in Displays)
            row.SelectedClip ??= LibraryClips.FirstOrDefault();
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
    /// The pending choices, read from the rows the user has been editing.
    /// </summary>
    /// <remarks>
    /// READS ONLY THE ROWS - NOT WallpaperEngine.ActivePlayers. It used to resolve a Pinned row
    /// against whatever that display happened to be playing at this instant, which made a config
    /// edit depend on transient engine state and produced the 2026-09-03 defect described on
    /// <see cref="DisplayRow.SelectedClip"/>. The row already knows which clip it means.
    ///
    /// A Pinned row whose SelectedClip is null - only possible with an empty library - yields a
    /// null file name, which SettingsDraft.Apply ignores rather than writing a pin to nowhere.
    /// </remarks>
    private Dictionary<string, DisplayChoice> BuildChoices()
    {
        var choices = new Dictionary<string, DisplayChoice>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Displays)
            choices[row.DeviceName] = new DisplayChoice(
                row.Mode, row.Mode == DisplayMode.Pinned ? row.SelectedClip : null);

        return choices;
    }
}
