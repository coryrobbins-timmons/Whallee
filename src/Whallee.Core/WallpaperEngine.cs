using Whallee.Core.Configuration;
using Whallee.Core.Desktop;
using Whallee.Core.Displays;
using Whallee.Core.Interop;
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

    /// <summary>
    /// The desktop layer the last successful pass attached to, or null if there has not been one.
    /// </summary>
    /// <remarks>
    /// THIS CACHE IS WHAT MAKES THE OVERLAPPED SWAP POSSIBLE, and it is not an optimisation.
    /// <see cref="DesktopLayer.Locate"/> sends a message to Explorer's desktop thread and blocks on
    /// it for up to a second; its own remarks spell out that calling it while host windows are
    /// attached converts that into a one-second freeze of the user's entire desktop — the fifth
    /// instance of the bug class this codebase has already fought four times. The swap keeps the
    /// outgoing windows alive on purpose, so it MUST NOT call Locate. It reuses this handle instead,
    /// which is sound precisely because the swap only ever runs when nothing about Explorer has
    /// changed: a clip or a scaling edit. Explorer restarts and display changes take the hard path,
    /// which tears down first and re-locates, exactly as before.
    /// </remarks>
    private DesktopLayerInfo? _layer;

    private bool _restarting;
    private bool _restartPending;

    /// <summary>Whether a pass queued during an in-flight pass needs the hard path.</summary>
    /// <remarks>
    /// Sticky across coalescing, deliberately. If an Explorer restart arrives while a clip swap is
    /// running, the coalesced retry must re-locate the desktop layer — servicing it as another swap
    /// would reuse a WorkerW handle that no longer exists.
    /// </remarks>
    private bool _pendingHard;

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
    /// <see cref="Start"/> pumps messages for about a second (mpv's liveness wait does; it is one
    /// window for the whole pass now, not one per display), so a
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
    public void Start() => Run(hard: true);

    /// <summary>
    /// Re-attaches after a CONFIG edit — a different clip, a different scaling mode — WITHOUT the
    /// desktop dropping to the Windows wallpaper in between.
    /// </summary>
    /// <remarks>
    /// The difference from <see cref="Start"/> is the order of teardown. Start stops everything and
    /// then builds; this builds the new generation on top of the old one, lets it come up, and only
    /// then retires the old. Since the outgoing video keeps playing underneath until the incoming
    /// one is painting over it, there is no moment with nothing on the wallpaper — and because the
    /// old generation is retired in one go at the end, every display changes together.
    ///
    /// FALLS BACK TO THE HARD PATH whenever the overlap is not safe: no cached layer, a cached
    /// WorkerW that Explorer has since destroyed, or the RaisedDesktop layout, whose z-order is set
    /// relative to the icon layer and has never run on any machine here. Callers do not choose —
    /// they ask for a config change and this decides.
    /// </remarks>
    public void ApplyChange() => Run(hard: !CanSwapInPlace());

    /// <summary>
    /// Whether the overlapped swap is safe right now. Conservative on purpose — every "no" costs
    /// only the flicker the app has always had, while a wrong "yes" is a stale window handle
    /// handed to SetParent.
    /// </summary>
    private bool CanSwapInPlace() =>
        _layer is { Mode: DesktopLayerMode.Classic } layer
        && _active.Count > 0
        && NativeMethods.IsWindow(layer.WorkerW);

    private void Run(bool hard)
    {
        if (_restarting)
        {
            _restartPending = true;
            _pendingHard |= hard;
            return;
        }

        _restarting = true;
        try
        {
            var thisPassIsHard = hard;
            do
            {
                // Cleared BEFORE the work, so an event arriving during this iteration is caught
                // by the loop condition rather than by the iteration that already passed it.
                _restartPending = false;
                _pendingHard = false;
                try
                {
                    // RE-CHECKED EVERY ITERATION, not just at entry. A coalesced pass can find the
                    // world different from when it was queued: a hard pass whose Locate failed
                    // clears the cached layer, and a swap queued before that would then run with
                    // no layer at all.
                    if (thisPassIsHard || !CanSwapInPlace()) StartOnce();
                    else SwapOnce();
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

                // Read BEFORE the loop condition clears it on the next iteration. A hard request
                // that arrived mid-pass must win: servicing it as a swap would reuse a WorkerW
                // handle Explorer has already destroyed.
                thisPassIsHard = _pendingHard;
            }
            while (_restartPending);
        }
        finally
        {
            _restarting = false;
            _restartPending = false;
            _pendingHard = false;
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

    /// <summary>
    /// The hard path: tear everything down, find the desktop layer, then build.
    /// </summary>
    /// <remarks>
    /// StopAll comes FIRST and must stay first. <see cref="DesktopLayer.Locate"/> blocks on
    /// Explorer's desktop thread, and doing that while any host window is still parented into
    /// Explorer freezes the user's whole desktop for the duration — see the remarks on Locate
    /// itself. The overlapped swap exists precisely so that the common case never has to come
    /// through here; see <see cref="SwapOnce"/>.
    /// </remarks>
    private void StartOnce()
    {
        StopAll();
        _errors.Clear();
        _generation++;

        var layer = DesktopLayer.Locate();
        if (layer is null)
        {
            // Cleared, not left stale: a WorkerW that cannot be found now must never be reused by
            // a later swap.
            _layer = null;
            _errors.Add("Could not find the desktop wallpaper layer (WorkerW).");
            return;
        }

        _layer = layer;
        BuildPlayers(layer);
        VerifyPlayersStarted();
    }

    /// <summary>
    /// The soft path: build the incoming generation ON TOP of the outgoing one, let it come up,
    /// then retire the outgoing one in a single step.
    /// </summary>
    /// <remarks>
    /// NO CALL TO <see cref="DesktopLayer.Locate"/> HERE, AND THERE MUST NEVER BE ONE. The whole
    /// point of this path is that the outgoing host windows are still parented into Explorer while
    /// it runs, and Locate blocks on Explorer's desktop thread — the combination is a one-second
    /// freeze of the user's entire desktop. <see cref="CanSwapInPlace"/> is what earns the right to
    /// reuse <see cref="_layer"/> instead: this path only ever runs for a config edit, where
    /// nothing about Explorer has changed, and it re-checks the handle is still a window first.
    ///
    /// If the incoming generation comes up empty — every clip unplayable, say — the outgoing one is
    /// put back rather than retired, so a bad Apply leaves the wallpaper as it was instead of
    /// blanking it. Exactly one generation is live when this returns, either way; the two are never
    /// both kept, which is what stops repeated failures stacking up players.
    /// </remarks>
    private void SwapOnce()
    {
        _errors.Clear();
        _generation++;

        var outgoingHosts = _hosts.ToList();
        var outgoingActive = _active.ToList();

        // The lists now describe the INCOMING generation only. The outgoing one is held in the
        // locals above and is still on screen, still playing.
        _hosts.Clear();
        _active.Clear();

        // TRUE UNTIL THE INCOMING GENERATION HAS EARNED THE SCREEN. If BuildPlayers throws — a
        // display enumeration failing mid-hotplug, say — the finally must put the working wallpaper
        // back, not retire it in favour of a half-built generation. Starting from false would blank
        // the desktop on any unexpected throw.
        var keepOutgoing = true;
        try
        {
            BuildPlayers(_layer!);
            VerifyPlayersStarted();
            keepOutgoing = _active.Count == 0;
        }
        finally
        {
            if (keepOutgoing)
            {
                Retire(_hosts, _active);
                _hosts.Clear();
                _active.Clear();
                _hosts.AddRange(outgoingHosts);
                _active.AddRange(outgoingActive);
            }
            else
            {
                Retire(outgoingHosts, outgoingActive);
            }
        }
    }

    /// <summary>Kills one generation's players and destroys its windows. Players first.</summary>
    /// <remarks>
    /// Order matters: destroying a host window out from under a live mpv leaves it rendering into
    /// a handle that no longer exists. <see cref="StopAll"/> does the same two steps in the same
    /// order for the same reason.
    /// </remarks>
    private static void Retire(List<HostWindow> hosts, List<ActivePlayer> players)
    {
        foreach (var player in players) player.Player.Dispose();
        foreach (var host in hosts) host.Dispose();
    }

    /// <summary>
    /// Attaches one player per display into <paramref name="layer"/>. Shared by both paths — the
    /// only difference between them is what happens before and after this runs.
    /// </summary>
    private void BuildPlayers(DesktopLayerInfo layer)
    {
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

                // In front of whatever else is in the layer. A no-op on the hard path, where this
                // is the only window on the monitor; load-bearing on the swap path, where the
                // outgoing generation is still sitting on the same monitor and would otherwise
                // cover this one. Classic only — CanSwapInPlace refuses the swap in the raised
                // layout precisely because HWND_TOP there would jump the desktop icons.
                if (layer.Mode == DesktopLayerMode.Classic)
                    host.BringToFront();

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

    /// <summary>
    /// Spends mpv's liveness window ONCE for the whole pass, then drops any player that died in it.
    /// </summary>
    /// <remarks>
    /// THIS IS WHY EVERY DISPLAY NOW LIGHTS UP TOGETHER. The wait used to live inside
    /// <see cref="MpvHost.Start"/>, so the loop above blocked for a second on each display in turn:
    /// three monitors meant a visible one-two-three cascade and a three-second pass. Launching every
    /// player and then waiting once costs ~1 s however many displays there are.
    ///
    /// Detection is not weakened — every player is still checked after at least a full window, and
    /// the earliest-launched ones now get a longer one than they used to.
    ///
    /// MessagePump.Sleep, never Thread.Sleep: this runs on a thread that owns windows parented into
    /// Explorer's desktop, and a non-pumping wait here freezes the user's whole desktop.
    /// </remarks>
    private void VerifyPlayersStarted()
    {
        if (_active.Count == 0) return;

        MessagePump.Sleep(MpvHost.LivenessWindow);

        // Collected first, removed after: mutating _active while enumerating it would throw, and
        // the failures are wanted in LastErrors either way.
        var dead = new List<ActivePlayer>();
        foreach (var active in _active)
        {
            var failure = active.Player.StartupFailure(active.ClipPath);
            if (failure is null) continue;

            _errors.Add($"{active.Display.DeviceName}: {failure}");
            dead.Add(active);
        }

        foreach (var active in dead)
        {
            // Safe on an already-exited process, and it is what releases the Process handle.
            active.Player.Dispose();
            _active.Remove(active);
        }

        // A dead player's HostWindow is deliberately LEFT IN PLACE, exactly as it was before: it is
        // an empty child window on the wallpaper layer, _hosts owns it, and StopAll destroys it on
        // the next pass. Tearing a window down mid-pass on the thread that owns it buys nothing.
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
