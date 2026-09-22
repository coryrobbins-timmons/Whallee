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
    /// Stage timings for the most recent pass, or null before the first one. See
    /// <see cref="PassProfile"/> for why this is measured rather than reasoned about.
    /// </summary>
    public string? LastPassProfile { get; private set; }

    /// <summary>
    /// What the most recent <c>sync</c> step actually did, or null if it has not run.
    /// </summary>
    /// <remarks>
    /// DIAGNOSTIC, added 2026-09-21, and deliberately separate from <see cref="LastErrors"/> - none
    /// of this is an error. The owner reported two displays aligning and a third lagging BEHIND
    /// them, every time, on the same monitor. The pass profile said "sync 18" and nothing else,
    /// which cannot distinguish two very different faults: a seek that was never sent (the pipe
    /// filter dropped that player) from a seek that was sent and did nothing (the player had not
    /// begun rendering, so rewinding it to zero was a no-op). Those need completely different
    /// fixes, so the log has to say which.
    /// </remarks>
    public string? LastSyncDetail { get; private set; }

    /// <summary>The pass currently being timed. Null outside a pass.</summary>
    private PassProfile? _profile;

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
        _profile = new PassProfile();
        _errors.Clear();

        _profile.Measure("stopAll", StopAll);
        _generation++;

        DesktopLayerInfo? layer = null;
        _profile.Measure("locate", () => layer = DesktopLayer.Locate());

        if (layer is null)
        {
            // Cleared, not left stale: a WorkerW that cannot be found now must never be reused by
            // a later swap.
            _layer = null;
            _errors.Add("Could not find the desktop wallpaper layer (WorkerW).");
            LastPassProfile = _profile.Describe("start (no layer)");
            return;
        }

        _layer = layer;
        BuildPlayers(layer);
        VerifyPlayersStarted();
        _profile.Measure("sync", AlignPlayers);
        _profile.Measure("reveal", RevealPlayers);

        LastPassProfile = _profile.Describe("start");
    }

    /// <summary>
    /// Shows this pass's host windows, all of them, in one loop.
    /// </summary>
    /// <remarks>
    /// THIS IS WHERE THE WALLPAPER ACTUALLY APPEARS, and doing it here rather than at window
    /// creation is what fixed two visible defects at once.
    ///
    /// A host window paints nothing of its own — its class brush is NULL — so while it is visible
    /// and mpv has not yet rendered, it shows Explorer's desktop wallpaper straight through. Windows
    /// used to be created visible, which meant every clip change flashed the user's Windows
    /// wallpaper for as long as mpv took to decode its first frame, and did so per monitor as each
    /// player warmed up at its own pace. Creating them hidden and revealing them together after the
    /// liveness window removes the flash and makes the monitors change as one.
    ///
    /// A reveal that fails is recorded and skipped rather than thrown: one monitor that will not
    /// show must not cost the others theirs.
    /// </remarks>
    private void RevealPlayers()
    {
        // Only the Classic layout re-orders on reveal. The raised layout's z-order was set against
        // the icon layer at attach time and HWND_TOP would jump the desktop icons.
        var bringToFront = _layer is { Mode: DesktopLayerMode.Classic };

        foreach (var host in _hosts)
        {
            try
            {
                host.Reveal(bringToFront);
            }
            catch (Exception ex)
            {
                _errors.Add($"could not show a host window: {ex.Message}");
            }
        }
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
        _profile = new PassProfile();
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

            // REVEALED BEFORE THE OUTGOING GENERATION IS RETIRED, in the try rather than after it,
            // so the new video is already on screen and in front when the old windows go. That
            // ordering is the entire no-gap guarantee.
            //
            // Skipped when nothing survived: revealing a set of empty windows over a working
            // wallpaper would replace it with Explorer's, which is the defect this exists to stop.
            if (!keepOutgoing)
            {
                // SAME GUARD AS THE REVEAL, deliberately. When nothing survived we keep the
                // outgoing generation on screen, and seeking players we are about to retire would
                // rewind the wallpaper the user is actually looking at.
                _profile.Measure("sync", AlignPlayers);
                _profile.Measure("reveal", RevealPlayers);
            }
        }
        finally
        {
            _profile.Measure("retire", () =>
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
            });

            LastPassProfile = _profile.Describe(keepOutgoing ? "swap (rolled back)" : "swap");
        }
    }

    /// <summary>
    /// Polls until every player has opened its IPC pipe and had a moment to draw, or one dies, or
    /// the cap runs out.
    /// </summary>
    /// <remarks>
    /// The rule itself is <see cref="PlayerSettle.Decide"/>, which is pure and tested. This method
    /// is only the clock, the process checks and the pumping wait — deliberately, because that
    /// split is what makes the rule testable at all without three monitors and a video file.
    /// </remarks>
    private void WaitForPlayersToSettle()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? readySince = null;

        while (true)
        {
            var anyDied = _active.Any(a => !a.Player.IsRunning);
            var allPipesUp = _active.All(a => a.Player.IsIpcPipeUp);

            // Recorded the first time every pipe is up, and never moved afterwards: the grace is
            // measured from when mpv finished initialising, not from the poll that noticed.
            if (allPipesUp) readySince ??= watch.Elapsed;

            var step = PlayerSettle.Decide(anyDied, allPipesUp, watch.Elapsed, readySince);
            if (step != SettleStep.Wait) return;

            MessagePump.Sleep(PlayerSettle.PollInterval);
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
        IReadOnlyList<string> libraryFiles = Array.Empty<string>();
        Exception? listFailure = null;
        _profile!.Measure("listLibrary", () =>
        {
            try { libraryFiles = _listFiles(_config.LibraryFolder); }
            catch (Exception ex) { listFailure = ex; }
        });

        if (listFailure is not null)
        {
            _errors.Add($"could not list {_config.LibraryFolder}: {listFailure.Message}");
            return;
        }

        var resolver = new ClipResolver(_ => libraryFiles, _random);

        var displays = System.Array.Empty<DisplayInfo>() as IReadOnlyList<DisplayInfo>;
        _profile.Measure("enumDisplays", () => displays = DisplayEnumerator.Enumerate());

        var windowsMs = 0L;
        var mpvMs = 0L;

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

                // HIDDEN until RevealPlayers. It paints nothing until mpv is rendering into it, and
                // an empty window is a hole showing Explorer's wallpaper.
                var windowWatch = System.Diagnostics.Stopwatch.StartNew();
                var host = HostWindow.CreateFor(display, layer);
                windowsMs += windowWatch.ElapsedMilliseconds;
                _hosts.Add(host);

                // Timed separately from the window because they have completely different suspects:
                // this one covers Process.Start plus two File.Exists checks against a
                // OneDrive-backed clip, either of which can block on a cloud placeholder.
                var mpvWatch = System.Diagnostics.Stopwatch.StartNew();
                var player = MpvHost.Start(
                    _config.MpvPath, host.Handle, clip, _config.Scaling,
                    PipeNameFor(ordinal), _guard);
                mpvMs += mpvWatch.ElapsedMilliseconds;

                _active.Add(new ActivePlayer(display, clip, player));
            }
            catch (Exception ex)
            {
                // One display failing must not take the others down.
                _errors.Add($"{display.DeviceName}: {ex.Message}");
            }
        }

        _profile.Record("createWindows", windowsMs);
        _profile.Record("launchMpv", mpvMs);
    }

    /// <summary>
    /// Waits ONCE for the whole pass for its players to settle, then drops any that died.
    /// </summary>
    /// <remarks>
    /// THIS IS WHY EVERY DISPLAY LIGHTS UP TOGETHER. The wait used to live inside
    /// <see cref="MpvHost.Start"/>, so the loop above blocked on each display in turn: three
    /// monitors meant a visible one-two-three cascade and a three-second pass. Launching every
    /// player and then waiting once costs the same however many displays there are.
    ///
    /// It POLLS rather than sleeping flat, which the pass profile said was worth doing: a measured
    /// swap ran 2159 ms with a full second of it spent asleep here regardless of what the players
    /// were doing. Detection improves as a side effect — a player that dies is now noticed within a
    /// poll interval instead of always costing the whole window.
    ///
    /// MessagePump.Sleep, never Thread.Sleep, for the poll interval as well: this runs on a thread
    /// that owns windows parented into Explorer's desktop, and a non-pumping wait here freezes the
    /// user's whole desktop.
    /// </remarks>
    private void VerifyPlayersStarted()
    {
        if (_active.Count == 0) return;

        _profile!.Measure("settle", WaitForPlayersToSettle);

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

    /// <summary>
    /// Brings every surviving player to the same position before anything is shown.
    /// </summary>
    /// <remarks>
    /// RUN BEFORE THE REVEAL, AND THAT ORDERING IS THE POINT. Aligning means rewinding whichever
    /// display got a head start - up to about 600 ms of video jumping backwards. The host windows
    /// are still hidden here, so that rewind is never seen. Doing this after RevealPlayers would
    /// trade a constant small offset for a visible hitch on every single swap.
    ///
    /// THE WAIT IS A POLLING PUMP, copied from WaitForPlayersToSettle, and it has to be. This
    /// thread owns windows re-parented into Explorer's desktop, so a non-pumping wait freezes the
    /// user's whole desktop - four separate bugs in this codebase have come from that. Note that
    /// MessagePump.RunUntil(Task) fits this job by NAME and must NOT be used: it is a blocking
    /// GetMessageW pump for the console harness, and in a WPF app it fights the framework's own
    /// message loop.
    ///
    /// Running out of the cap is not an error and is not reported. It means the wallpaper appears
    /// with the players possibly unaligned, which is exactly what happened before this existed. The
    /// background task is left to finish on its own after being told to stop early - see the
    /// cancellation remarks below - it never throws past this method regardless.
    ///
    /// EVERYTHING IN THIS METHOD IS WRAPPED IN A TRY/CATCH THAT SWALLOWS. This is the first throwing
    /// statement in both StartOnce and SwapOnce's try block after the incoming generation is built,
    /// and MessagePump.Sleep is a documented throw site: it raises InvalidOperationException when
    /// MsgWaitForMultipleObjectsEx returns WAIT_FAILED (a desktop switch, a session lock, handle
    /// exhaustion). If that propagated out of here, RevealPlayers would never run - the incoming
    /// host windows would stay hidden - and in SwapOnce, keepOutgoing (already latched false by this
    /// point) would make the finally retire the OUTGOING generation, the one that was actually
    /// working. The user would be left staring at Explorer's wallpaper with three live, invisible
    /// mpv processes: exactly the defect SwapOnce exists to prevent. RevealPlayers already guards
    /// per-host for the identical reason (one monitor's failure must not cost the others theirs);
    /// this is the same rule one level up - a failed alignment must never cost the reveal.
    /// </remarks>
    private void AlignPlayers()
    {
        try
        {
            // FILTERED HERE, NOT TRUSTED FROM SETTLE. The design premise that "settle already gates
            // on IsIpcPipeUp for every survivor, so pipes are known to be up by the time this runs"
            // does not hold: PlayerSettle.Decide returns PlayerDied before ever consulting
            // allPipesUp, and TimedOut means the cap was reached with allPipesUp still false. Both
            // exits leave WaitForPlayersToSettle's survivors in _active without their pipe having
            // been confirmed, so this re-checks the cheap, non-blocking MpvHost.IsIpcPipeUp itself
            // rather than assuming the wait already proved it.
            var readyPlayers = _active.Where(a => a.Player.IsIpcPipeUp).ToArray();

            // Named in LAUNCH order, which is how _active lists them, so the log names displays in
            // a consistent, predictable order run to run. See LastSyncDetail.
            var ready = string.Join(", ", readyPlayers.Select(a => a.Display.DeviceName));

            // NO "FEWER THAN TWO, SKIP" SHORTCUT ANY MORE. It was a correct optimisation while this
            // step only seeked - one display cannot be out of step with anything. It is a trap for a
            // step that pauses, because every early return is a chance to leave a paused player
            // unresumed. The step now runs whenever there is a player to talk to.
            if (readyPlayers.Length == 0)
            {
                LastSyncDetail = $"sync skipped: {_active.Count} active, none with a live pipe";
                return;
            }

            // _active - and therefore readyPlayers - is in LAUNCH order. All players are paused
            // before any of them is seeked now, so seek order no longer affects the spread between
            // players; this ordering is kept only because it is what _active already gives us, not
            // because anything downstream depends on it.
            var pipeNames = readyPlayers.Select(a => a.Player.PipeName).ToArray();

            // Cancelled unconditionally in the finally below, whether the wait loop exits because the
            // work finished or because the cap elapsed. This ends AlignAsync's WAIT (Phase 1) and, if
            // the cap elapses before or right at the wait finishing, stops the pause/seek/resume burst
            // from starting at all - see the cancellation check AlignAsync makes immediately before
            // Phase 2. It does NOT reach any further than that: once a player has actually been
            // paused, AlignAsync no longer consults this token at all, and nothing here can stop or
            // speed up the seek/resume that follows. Do not read this as "the burst is bounded by
            // Timeout" - it is not, and shortening PlayerSync.Timeout on that assumption is exactly
            // the mistake that produced the all-timeouts regression on 2026-09-21. If this token is
            // cancelled after Phase 2 has begun, the burst still runs to completion, and the finally
            // in AlignAsync still resumes everything that was paused.
            using var cts = new CancellationTokenSource();
            try
            {
                // OWNED BY THIS METHOD, NOT RETURNED BY THE TASK, so the measurements survive a
                // timeout. Returning them meant they could only be read once the task had completed
                // successfully - and a timeout is precisely the case where knowing how far it got is
                // most useful. The task fills this as it goes and we read whatever landed.
                var observed = new double?[readyPlayers.Length];

                var work = PlayerSync.AlignAsync(pipeNames, observed, cts.Token);

                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!work.IsCompleted && watch.Elapsed < PlayerSync.Timeout)
                    MessagePump.Sleep(PlayerSync.PollInterval);

                // Result is only safe to read once the task has completed - reading it earlier would
                // BLOCK, on the one thread in this process where blocking freezes the user's whole
                // desktop. When the cap elapsed first, how many landed is exactly what we do not
                // know, and saying so is more useful than a number that might be a lie.
                var report = work.IsCompletedSuccessfully ? work.Result : default;

                LastSyncDetail = !work.IsCompletedSuccessfully
                    ? $"sync TIMED OUT after {PlayerSync.Timeout.TotalMilliseconds:F0} ms: "
                      + $"{_active.Count} active, {readyPlayers.Length} with a live pipe ({ready}); "
                      + $"last positions seen: {PlayerSync.Describe(observed)}"
                    : !report.Ready
                        ? $"sync gave up waiting: {readyPlayers.Length} with a live pipe ({ready}); "
                          + $"last positions seen: {PlayerSync.Describe(observed)}"
                        : $"sync: {readyPlayers.Length} with a live pipe ({ready}); "
                          + $"paused {report.Paused}, seeked {report.Seeked}, "
                          + $"resumed {report.Resumed}, retried {report.Retried}; "
                          + $"positions when ready: {PlayerSync.Describe(observed)}";
            }
            finally
            {
                cts.Cancel();
            }
        }
        catch (Exception)
        {
            // Swallowed on purpose - see the remarks above this method. A failed alignment must
            // never cost the reveal: the wallpaper appearing possibly-unaligned is the pre-existing,
            // accepted behaviour this class replaced, and it is always better than the reveal not
            // happening at all.
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
