using Whallee.Core.Configuration;
using Whallee.Core.Desktop;
using Whallee.Core.Displays;
using Whallee.Core.Playback;

namespace Whallee.Core;

/// <summary>What is playing on one display right now.</summary>
/// <remarks>
/// Exists so a consumer can map a display to its player — and therefore to its IPC pipe name —
/// without re-deriving a loop index. The index is not a reliable key: a display whose clip fails
/// to resolve is skipped, so the nth player is not necessarily the nth display.
/// </remarks>
public sealed record ActivePlayer(DisplayInfo Display, string ClipPath, MpvHost Player);

public sealed class WallpaperEngine : IDisposable
{
    private readonly WhalleeConfig _config;
    private readonly Func<string, IReadOnlyList<string>> _listFiles;
    private readonly Random _random;
    private readonly JobObjectGuard _guard = new();
    private readonly List<HostWindow> _hosts = new();
    private readonly List<ActivePlayer> _active = new();
    private readonly List<string> _errors = new();
    private readonly Action<string>? _onStartFailure;

    /// <summary>Incremented on every start pass; keeps IPC pipe names unique across generations.</summary>
    private int _generation;

    private bool _restarting;
    private bool _restartPending;

    /// <summary>Errors from the most recent start pass. A snapshot — the live list is not exposed.</summary>
    public IReadOnlyList<string> LastErrors => _errors.ToArray();

    /// <summary>
    /// Which clip and which player are on which display right now. A snapshot; each entry carries
    /// the player, so <c>ActivePlayers[i].Player.PipeName</c> is how Plan 2 finds a display's IPC
    /// pipe without restarting anything.
    /// </summary>
    public IReadOnlyList<ActivePlayer> ActivePlayers => _active.ToArray();

    /// <param name="config">Live config. <see cref="Start"/> adds an assignment for any new display.</param>
    /// <param name="onStartFailure">
    /// Optional. Called the moment a whole-pass failure happens, so it is visible even when a
    /// coalesced retry immediately supersedes it and clears <see cref="LastErrors"/>. Called on the
    /// pump thread from inside the start loop, so it MUST NOT block and MUST NOT throw — hand it
    /// something that only enqueues. Per-display errors are not routed here; they survive in
    /// <see cref="LastErrors"/> for the caller to read after the pass.
    /// </param>
    public WallpaperEngine(WhalleeConfig config, Action<string>? onStartFailure = null)
    {
        _config = config;
        _onStartFailure = onStartFailure;
        _listFiles = ClipResolver.ListLibraryFiles;
        _random = new Random();
    }

    /// <summary>
    /// Tears down whatever is running and attaches a player to every current display.
    /// </summary>
    /// <remarks>
    /// AMENDED 2026-08-27: re-entrancy is now guarded here, not only in <see cref="Restart"/>.
    /// <see cref="Start"/> pumps messages for up to ~3 s (mpv's liveness wait does), so a
    /// <c>WM_DISPLAYCHANGE</c> can be dispatched while it is running. That was unreachable only by
    /// statement order in the harness — <c>Start()</c> was called before the watcher existed — and
    /// the natural order for Plan 2's tray app, watcher first, reopens it: the re-entrant call
    /// would find <c>_restarting == false</c>, run a full nested pass, and then the outer loop
    /// would resume and append a SECOND generation of hosts and mpv processes for the remaining
    /// displays. Both entry points now share one guard.
    ///
    /// The guard coalesces rather than drops. <c>WM_DISPLAYCHANGE</c> arrives in bursts and each
    /// pass snapshots the display layout at entry, so dropping a re-entrant call would let the
    /// burst's *later* messages — including the one signalling that the topology had settled —
    /// vanish, leaving the engine on a stale layout with nothing left to re-trigger it. A
    /// re-entrant call therefore sets <c>_restartPending</c> and the in-flight call loops again.
    ///
    /// Plain bools are correct: every caller is the pump thread, so this is single-threaded by
    /// construction. Do not add locks or Interlocked.
    /// </remarks>
    public void Start()
    {
        if (_restarting)
        {
            _restartPending = true;
            return;
        }

        _restarting = true;
        try
        {
            do
            {
                // Cleared BEFORE the work, so an event arriving during this iteration is caught
                // by the loop condition rather than by the iteration that already passed it.
                _restartPending = false;
                try
                {
                    StartOnce();
                }
                catch (Exception ex)
                {
                    // A failure from outside the per-display try (the desktop layer, the display
                    // enumeration) must not escape with _restartPending still set, or a coalesced
                    // event is dropped with nothing left to re-trigger it and the engine sits torn
                    // down until the user notices. Record it and let the loop condition decide: if
                    // an event did arrive, the next iteration runs and supersedes this failure.
                    // Reporting through LastErrors rather than throwing is deliberate — every
                    // caller is either the harness, which prints LastErrors and exits non-zero, or
                    // a WndProc, where a throw could only be caught and logged anyway.
                    _errors.Add($"start failed: {ex.Message}");

                    // Also reported HERE, at the point of failure, and not left to LastErrors
                    // alone: _errors is cleared at the top of every pass, so a whole-pass failure
                    // that a coalesced retry then superseded would be wiped before anybody read it,
                    // leaving no trace of it at all. "It failed, retried, and recovered" is exactly
                    // the diagnostic a misbehaving hotplug calls for. _restartPending is already
                    // set by any event that arrived during the failing pass, so the wording can say
                    // which of the two outcomes this is.
                    ReportStartFailure(_restartPending
                        ? $"start attempt failed, a display change is queued so retrying: {ex.Message}"
                        : $"start attempt failed, giving up on this pass: {ex.Message}");
                }
            }
            while (_restartPending);
        }
        finally
        {
            _restarting = false;
            _restartPending = false;
        }
    }

    /// <summary>Tear everything down and re-attach. Used after Explorer restarts or displays change.</summary>
    /// <remarks>
    /// <see cref="Start"/> is non-additive — it opens with <c>StopAll()</c> — and now carries the
    /// coalescing guard itself, so restarting is exactly starting again. The former
    /// <c>StopAll(); Start();</c> body called <c>StopAll()</c> twice per restart; this stays as a
    /// named entry point because it is what the callers mean.
    /// </remarks>
    public void Restart() => Start();

    /// <summary>
    /// Hands a whole-pass failure to the caller's reporter, if it supplied one. Guarded: a reporter
    /// that throws must not abort the start loop — least of all from inside the catch that exists to
    /// keep the loop alive in the first place.
    /// </summary>
    private void ReportStartFailure(string message)
    {
        try
        {
            _onStartFailure?.Invoke(message);
        }
        catch
        {
            // Nothing left to report it to.
        }
    }

    private void StartOnce()
    {
        StopAll();
        _errors.Clear();
        _generation++;

        var layer = DesktopLayer.Locate();
        if (layer is null)
        {
            _errors.Add("Could not find the desktop wallpaper layer (WorkerW).");
            return;
        }

        // Listed ONCE per pass rather than once per display. This is Directory.EnumerateFiles over
        // a OneDrive-backed folder running on the pump thread, so three displays meant three
        // chances to block on a sync. ClipResolver's injected delegate is untouched — every
        // display simply gets the same materialised snapshot.
        IReadOnlyList<string> libraryFiles;
        try
        {
            libraryFiles = _listFiles(_config.LibraryFolder);
        }
        catch (Exception ex)
        {
            _errors.Add($"could not list {_config.LibraryFolder}: {ex.Message}");
            return;
        }

        var resolver = new ClipResolver(_ => libraryFiles, _random);
        var displays = DisplayEnumerator.Enumerate();

        for (var ordinal = 0; ordinal < displays.Count; ordinal++)
        {
            var display = displays[ordinal];
            try
            {
                // MatchAssignment and Resolve sit INSIDE this try deliberately: they used to sit
                // outside it, so a single IOException reaching out of the library folder aborted
                // the whole pass — including displays that had already succeeded — in direct
                // contradiction of the comment in the catch below.
                var assignment = MatchAssignment(display);
                var clip = resolver.Resolve(assignment, _config.LibraryFolder);
                if (clip is null)
                {
                    _errors.Add($"{display.DeviceName}: no playable clip in {_config.LibraryFolder}");
                    continue;
                }

                var host = HostWindow.CreateFor(display, layer);
                _hosts.Add(host);
                _active.Add(new ActivePlayer(display, clip, MpvHost.Start(
                    _config.MpvPath, host.Handle, clip, _config.Scaling,
                    PipeNameFor(ordinal), _guard)));
            }
            catch (Exception ex)
            {
                // One display failing must not take the others down.
                _errors.Add($"{display.DeviceName}: {ex.Message}");
            }
        }
    }

    /// <summary>The IPC pipe name for one display in the current generation.</summary>
    /// <remarks>
    /// It used to be <c>whallee-{loop index}</c>, which collides with a dying predecessor.
    /// <see cref="StopAll"/> calls <c>Process.Kill</c> and returns without waiting — correctly so,
    /// since a blocking wait there is the very bug class this branch has fought repeatedly — so the
    /// next pass reused <c>whallee-0</c> while the old mpv might still hold
    /// <c>\\.\pipe\whallee-0</c>. mpv treats a failed pipe bind as non-fatal, so the wallpaper
    /// looked perfect while that display's IPC pipe silently did not exist, and nothing detected it
    /// until the next restart.
    ///
    /// The <c>whallee-</c> prefix is spec-mandated (spec line 210). The three fields after it make
    /// collision impossible by construction rather than merely unlikely:
    /// <list type="bullet">
    /// <item>process id — separates us from any other Whallee process, live or dying;</item>
    /// <item>generation — increments on every pass, so no pass can reuse a name an earlier pass in
    /// this process handed out;</item>
    /// <item>display ordinal — unique within a pass.</item>
    /// </list>
    /// A hash of the display's DeviceId was considered for the last field and rejected: a hash
    /// cannot be *proven* collision-free, and since <see cref="ActivePlayers"/> exposes the name
    /// directly, nobody needs to re-derive it from display identity. The only residual case is PID
    /// reuse, which requires this process to have exited — at which point the job object has
    /// already killed every mpv that held one of our pipes.
    /// </remarks>
    private string PipeNameFor(int ordinal) =>
        $"whallee-{Environment.ProcessId}-{_generation}-{ordinal}";

    /// <summary>
    /// Finds this display's saved assignment, adding a Shuffle default to config for a display we
    /// have never seen. The decision itself lives in <see cref="DisplayMatcher"/>, which is pure
    /// and tested; this wrapper only applies the side effect.
    /// </summary>
    private DisplayAssignment MatchAssignment(DisplayInfo display)
    {
        var (assignment, created) = DisplayMatcher.Resolve(_config.Displays, display);
        if (created)
            _config.Displays.Add(assignment);
        return assignment;
    }

    public void StopAll()
    {
        foreach (var active in _active) active.Player.Dispose();
        _active.Clear();
        foreach (var host in _hosts) host.Dispose();
        _hosts.Clear();
    }

    public void Dispose()
    {
        StopAll();
        _guard.Dispose();
    }
}
