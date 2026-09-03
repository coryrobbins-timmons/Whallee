using System.Diagnostics;
using Whallee.Core;
using Whallee.Core.Configuration;
using Whallee.Core.Desktop;

namespace Whallee.App.Services;

/// <summary>
/// Owns the wallpaper engine and the shell watcher, both bound to the WPF UI thread.
/// </summary>
/// <remarks>
/// THREAD AFFINITY IS NOT OPTIONAL. HostWindow uses SetParent to attach windows this process owns
/// into Explorer's desktop hierarchy, which attaches the two threads' input queues. The thread that
/// creates those windows must keep pumping for as long as they exist, or the USER'S DESKTOP freezes
/// - icons stop responding and the wait cursor appears system-wide. WPF's Dispatcher thread pumps,
/// so that is the thread we use, and every method here must be called on it.
///
/// Do NOT call MessagePump.RunUntil anywhere in this app: it runs its own blocking GetMessageW loop
/// and would fight the Dispatcher. It exists for the console harness only. MessagePump.WaitForExit
/// and MessagePump.Sleep ARE still used - indirectly, inside MpvHost and DesktopLayer - and are what
/// keep both the UI and the desktop responsive during the ~1s-per-display mpv startup.
///
/// Corollary for callers: never run slow work on this thread. Thumbnails, file copies and hydration
/// waits belong on a background thread.
/// </remarks>
public sealed class EngineHost : IDisposable
{
    private readonly UiReporter _reporter;
    private readonly string _configPath;
    private WallpaperEngine? _engine;
    private ShellWatcher? _watcher;
    private bool _disposed;

    public WhalleeConfig Config { get; }

    public EngineHost(UiReporter reporter)
    {
        _reporter = reporter;
        _configPath = ConfigStore.DefaultPath;
        Config = ConfigStore.Load(_configPath);
        SeedDefaults();
    }

    public IReadOnlyList<string> LastErrors =>
        _engine?.LastErrors ?? Array.Empty<string>();

    /// <summary>
    /// What each display is playing right now.
    /// </summary>
    /// <remarks>
    /// THE ONLY PLACE A SHUFFLING DISPLAY'S CURRENT CLIP IS KNOWABLE. ClipResolver picks it at
    /// random when the engine starts and nothing writes it to config, so config can answer this
    /// question for a pinned display and only for a pinned display.
    ///
    /// Empty before Start() and after Dispose(), rather than throwing - callers are UI code asking
    /// "what is on screen", and "nothing yet" is a real answer.
    /// </remarks>
    public IReadOnlyList<ActivePlayer> ActivePlayers =>
        _engine?.ActivePlayers ?? Array.Empty<ActivePlayer>();

    public void Start()
    {
        // Saved BEFORE Start(). No host window exists yet, so nothing shares an input queue with
        // Explorer: this is the only window in which synchronous disk I/O is unconditionally safe.
        // It also means a Start() that fails outright still leaves the seeded config on disk.
        // Mirrors Whallee.Harness, which is the reviewed reference for this sequence.
        ConfigStore.Save(Config, _configPath);

        // onStartFailure is not optional decoration. A whole-pass failure that a coalesced retry
        // then supersedes is cleared from LastErrors before ReportOutcome ever reads it, so it has
        // to reach the log at the moment it happens or not at all. UiReporter.Report only enqueues,
        // which is what this callback requires - it fires on the pump thread.
        _engine = new WallpaperEngine(Config, onStartFailure: _reporter.Report);

        // The watcher is constructed BEFORE Start(). That order is safe because Plan 1's final fix
        // wave routed Start() through the same re-entrancy guard as Restart() - a display change
        // arriving during the initial Start() is coalesced, not nested. Before that fix this order
        // would have created a second generation of host windows and mpv processes.
        _watcher = new ShellWatcher(
            onShellRestarted: () =>
            {
                _reporter.Report("explorer restarted — re-attaching");
                Restart();
            },
            onDisplaysChanged: () =>
            {
                _reporter.Report("displays changed — re-attaching");
                Restart();
            },
            // Enqueue-only, as the parameter requires: this runs inside the WndProc.
            onError: _reporter.Report);

        _engine.Start();
        ReportOutcome();

        // ACCEPTED EXPOSURE, carried over from the harness with its reasoning intact: this second
        // save is synchronous disk I/O on the thread that now owns the host windows, so a stalled
        // write stalls the user's desktop with it. It cannot move earlier - the display assignments
        // it persists are CREATED by Start() itself, and they are what the settings page in Task 9
        // binds to. Without it, a first run on a fresh machine leaves Displays empty on disk.
        // It is a small write to LocalApplicationData on a local disk, once per run.
        ConfigStore.Save(Config, _configPath);
    }

    public void Restart()
    {
        if (_engine is null) return;
        _engine.Restart();
        ReportOutcome();
    }

    /// <summary>
    /// Persists config WITHOUT re-attaching. For changes the running players do not depend on -
    /// a discovered ffmpeg path, for instance. Restarting three mpv processes to record where an
    /// executable lives would be a visible flicker for nothing.
    /// </summary>
    public void SaveConfig() => ConfigStore.Save(Config, _configPath);

    /// <summary>Persists the current config and re-attaches. Used after the UI changes settings.</summary>
    public void ApplyConfigChange()
    {
        SaveConfig();
        Restart();
    }

    private void ReportOutcome()
    {
        foreach (var error in LastErrors)
            _reporter.Report($"error: {error}");
    }

    private void SeedDefaults()
    {
        // First run only. Matches the harness's seed values exactly so the two entry points cannot
        // disagree about where the library and mpv live.
        if (string.IsNullOrEmpty(Config.LibraryFolder))
        {
            Config.LibraryFolder =
                @"C:\Users\cory.robbins\OneDrive - Timmons Group Inc\Documents\coryrob\Whallee\Library";
            Config.MpvPath =
                @"C:\Users\cory.robbins\AppData\Local\Programs\Lively Wallpaper\Plugins\Mpv\mpv.exe";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // TIMED, and the timings are kept rather than being scaffolding. Teardown runs on the
        // thread that still owns host windows parented into Explorer's desktop, so anything slow
        // here is felt by the USER as system-wide input lag - measured at roughly a second. These
        // three numbers are what say which phase is responsible, and they cost nothing.
        var total = Stopwatch.StartNew();

        // Watcher first: stop new events arriving before tearing the engine down. A restart
        // dispatched into a half-disposed engine is the kind of race that only shows up on the
        // owner's machine, at shutdown, once.
        var step = Stopwatch.StartNew();
        _watcher?.Dispose();
        var watcherMs = step.ElapsedMilliseconds;

        // StopAll before Dispose so the players-and-windows phase is timed separately from the job
        // object. Dispose calls StopAll again, but by then both lists are empty and it is a no-op.
        step.Restart();
        _engine?.StopAll();
        var stopAllMs = step.ElapsedMilliseconds;

        step.Restart();
        _engine?.Dispose();
        var guardMs = step.ElapsedMilliseconds;

        _reporter.Report(
            $"teardown {total.ElapsedMilliseconds} ms " +
            $"(watcher {watcherMs}, players+windows {stopAllMs}, job object {guardMs})");
    }
}
