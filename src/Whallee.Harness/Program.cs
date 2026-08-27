using System.Collections.Concurrent;
using Whallee.Core;
using Whallee.Core.Configuration;
using Whallee.Core.Desktop;
using Whallee.Core.Displays;
using Whallee.Core.Playback;

// EXIT CODES — the single place they are defined. A caller scripting this harness needs to tell
// "partly up" from "nothing up" without parsing text, so 3 and 6 are deliberately distinct.
//
//   0  success. Includes the legitimate "no displays detected" case: nothing to start, no errors.
//   1  usage error, or an unknown verb.
//   2  the desktop wallpaper layer (WorkerW) could not be found — from the pre-flight check in
//      `workerw` and `one`. In `all` the engine reports it as an error instead, so that surfaces
//      as 6 with the reason on the console.
//   3  running, but degraded: at least one display IS playing and at least one reported an error.
//   4  the `one` verb threw.
//   5  the `all` verb threw.
//   6  the `all` verb started nothing: zero players despite errors being reported. Nothing is on
//      screen. Distinct from 0, where zero players simply means there was nothing to start.

if (args.Length == 0)
{
    Console.WriteLine("usage: Whallee.Harness <displays | workerw | one <video-path> | all>");
    return 1;
}

switch (args[0])
{
    case "displays":
        foreach (var display in DisplayEnumerator.Enumerate())
        {
            Console.WriteLine(display);
            Console.WriteLine($"    id: {display.DeviceId}");
        }
        return 0;

    case "workerw":
    {
        var layer = DesktopLayer.Locate();
        if (layer is null)
        {
            Console.WriteLine("FAILED: no WorkerW found. The engine cannot work on this system.");
            return 2;
        }
        Console.WriteLine($"mode:         {layer.Mode}");
        Console.WriteLine($"progman:      0x{layer.Progman:X}");
        Console.WriteLine($"workerW:      0x{layer.WorkerW:X}");
        Console.WriteLine($"shellDefView: 0x{layer.ShellDefView:X}");
        return 0;
    }

    // Both playback verbs are wrapped: they create host windows and mpv processes, and an
    // exception escaping to the runtime's default handler prints a raw stack trace over a desktop
    // that may still have wallpapers attached. Distinct exit codes so a caller can tell which verb
    // failed without parsing the text. (1 = usage, 2 = no WorkerW, 3 = ran with per-display errors.)
    case "one":
        try
        {
            return RunOne(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED (one): {ex.Message}");
            return 4;
        }

    case "all":
        try
        {
            return RunAll();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED (all): {ex.Message}");
            return 5;
        }

    default:
        Console.WriteLine($"unknown verb: {args[0]}");
        return 1;
}

static int RunOne(string[] args)
{
    if (args.Length < 2)
    {
        Console.WriteLine("usage: Whallee.Harness one <video-path>");
        return 1;
    }
    var videoPath = args[1];
    const string mpvPath =
        @"C:\Users\cory.robbins\AppData\Local\Programs\Lively Wallpaper\Plugins\Mpv\mpv.exe";

    var layer = DesktopLayer.Locate();
    if (layer is null)
    {
        Console.WriteLine("FAILED: no WorkerW");
        return 2;
    }
    Console.WriteLine($"desktop layer: {layer.Mode}, workerW 0x{layer.WorkerW:X}");

    var display = DisplayEnumerator.Enumerate().First(d => d.IsPrimary);
    Console.WriteLine($"target: {display}");

    // Declared first so it is disposed LAST, after the host window is gone. Everything printed
    // from here on goes through it, because from CreateFor onwards this thread's input queue is
    // attached to Explorer's and a console write is not a bounded operation.
    using var reporter = new ConsoleReporter();

    using var guard = new JobObjectGuard();
    using var host = HostWindow.CreateFor(display, layer);
    reporter.Report($"host window: 0x{host.Handle:X}");

    using var mpv = MpvHost.Start(
        mpvPath, host.Handle, videoPath, ScalingMode.FillCrop, "whallee-one", guard);
    reporter.Report($"mpv pid {mpv.ProcessId} on pipe {mpv.PipeName} — press Enter to stop");

    // Read the line on a background thread so this one — which owns the host window,
    // and whose input queue Explorer's desktop is now attached to — keeps pumping.
    MessagePump.RunUntil(Task.Run(Console.ReadLine));
    return 0;
}

static int RunAll()
{
    var configPath = ConfigStore.DefaultPath;
    var config = ConfigStore.Load(configPath);

    // First run: seed sensible values for this machine.
    if (string.IsNullOrEmpty(config.LibraryFolder))
    {
        config.LibraryFolder =
            @"C:\Users\cory.robbins\OneDrive - Timmons Group Inc\Documents\coryrob\Whallee\Library";
        config.MpvPath =
            @"C:\Users\cory.robbins\AppData\Local\Programs\Lively Wallpaper\Plugins\Mpv\mpv.exe";
    }

    // Saved BEFORE Start(). No host window exists yet, so nothing shares an input queue with
    // Explorer: this is the only window in which synchronous disk I/O is unconditionally safe.
    // It also means a Start() that fails outright still leaves the seeded config on disk.
    ConfigStore.Save(config, configPath);
    Console.WriteLine($"config written to {configPath}");

    // Declared before the engine so it is disposed AFTER it: engine.Dispose destroys the host
    // windows, which breaks the SetParent input-queue attachment, and only then does the reporter
    // wait for its printer thread.
    using var reporter = new ConsoleReporter();

    // The reporter doubles as the engine's whole-pass failure channel: a failure that a coalesced
    // retry supersedes is cleared from LastErrors before Describe ever runs, so it has to reach the
    // console at the moment it happens or not at all. Enqueue-only, which is what that callback
    // requires — it fires on the pump thread.
    using var engine = new WallpaperEngine(config, onStartFailure: reporter.Report);
    engine.Start();

    foreach (var line in Describe(engine))
        reporter.Report(line);

    // ACCEPTED EXPOSURE, kept deliberately: this second save is synchronous disk I/O on the thread
    // that now owns the host windows, so a stalled write stalls the user's desktop with it. It
    // cannot move earlier — the display assignments it persists are created by Start() itself, and
    // they are what makes hand-editing config.json to pin a clip per display possible at all.
    // It is a small write to LocalApplicationData on a local disk, and it happens once per run.
    ConfigStore.Save(config, configPath);

    reporter.Report("running — press Enter to stop");

    using var watcher = new ShellWatcher(
        onShellRestarted: () =>
        {
            reporter.Report("explorer restarted — re-attaching");
            RestartAndReport();
        },
        onDisplaysChanged: () =>
        {
            reporter.Report("displays changed — re-attaching");
            RestartAndReport();
        },
        // Enqueue-only, as the parameter requires: this runs inside the WndProc.
        onError: reporter.Report);

    // AMENDED 2026-08-25: must pump, not Console.ReadLine(). This thread owns the host
    // windows, and SetParent attached its input queue to Explorer's desktop thread — a
    // thread that stops pumping freezes the user's desktop. See Task 13 Step 3.
    MessagePump.RunUntil(Task.Run(Console.ReadLine));

    // Reflects the FINAL state, not any superseded attempt — see the exit-code table at the top.
    // Zero players with no errors is the legitimate "no displays detected" case, so it stays 0;
    // zero players WITH errors means nothing is on screen and gets its own code.
    if (engine.LastErrors.Count == 0)
        return 0;
    return engine.ActivePlayers.Count == 0 ? 6 : 3;

    void RestartAndReport()
    {
        engine.Restart();
        // Without this, a restart that failed reported nothing at all: LastErrors was only ever
        // printed for the initial Start().
        foreach (var line in Describe(engine))
            reporter.Report(line);
    }
}

/// <summary>
/// One line per error, then one line per display saying what actually landed on it. Without the
/// second half a wrong-clip result cannot be diagnosed from the console at all.
/// </summary>
static IEnumerable<string> Describe(WallpaperEngine engine)
{
    foreach (var error in engine.LastErrors)
        yield return $"error: {error}";

    foreach (var active in engine.ActivePlayers)
        yield return $"{active.Display.DeviceName,-16} pid {active.Player.ProcessId,-6} " +
                     $"pipe {active.Player.PipeName,-24} clip {Path.GetFileName(active.ClipPath)}";
}

/// <summary>
/// Prints to the console from a dedicated thread, so that no caller ever performs console I/O
/// itself.
/// </summary>
/// <remarks>
/// This exists because <c>Console.WriteLine</c> is not a bounded operation. QuickEdit selection is
/// on by default in both conhost and Windows Terminal, so if the user click-drags in this window —
/// to copy a PID out of the output, say — every console write suspends until they press Escape.
/// The same happens when stdout is a pipe (<c>| tee log.txt</c>) whose reader stalls. The watcher
/// callbacks run inside a WndProc dispatched by the pump thread, whose input queue SetParent has
/// attached to Explorer's desktop, so a suspended write there does not just delay a log line: it
/// freezes the user's whole desktop, system-wide, until they press Escape in a window they have no
/// reason to associate with the freeze. That was the fourth instance in this branch of "something
/// blocking ran on the pump thread".
///
/// A drain point inside <c>MessagePump.RunUntil</c>'s loop would NOT have fixed it — the pump
/// thread would still be the thread performing the write. Only a genuinely different thread does,
/// which is what this is. Callers touch nothing but <see cref="Report"/>, which enqueues and
/// returns.
///
/// The queue is deliberately unbounded: a bounded one would block <see cref="Report"/> once full,
/// reintroducing on the pump thread exactly the stall this type exists to prevent. Volume is a
/// handful of lines per restart, so unbounded growth is not a practical concern.
/// </remarks>
sealed class ConsoleReporter : IDisposable
{
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _printer;
    private bool _disposed;

    public ConsoleReporter()
    {
        _printer = new Thread(Print) { IsBackground = true, Name = "whallee-console" };
        _printer.Start();
    }

    /// <summary>
    /// Queues one line for printing. Safe to call from a WndProc: it never touches the console,
    /// never blocks, and never throws.
    /// </summary>
    public void Report(string line)
    {
        try
        {
            _lines.Add(line);
        }
        // Covers both shutdown races: InvalidOperationException once adding is completed, and
        // ObjectDisposedException (which derives from it) once the collection is gone.
        catch (InvalidOperationException) { }
    }

    private void Print()
    {
        foreach (var line in _lines.GetConsumingEnumerable())
        {
            try
            {
                Console.WriteLine(line);
            }
            catch (Exception)
            {
                // EVERYTHING, not just IOException. This runs on a background thread, where an
                // unhandled exception terminates the whole process — and it would do so during
                // teardown, with mpv processes mid-kill. A printer thread must never be able to
                // kill the process: dropping a diagnostic line is always the better outcome.
                // Keep draining either way, so no producer is ever left waiting on a full queue.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lines.CompleteAdding();

        // Bounded wait: if the console is still suspended by a QuickEdit selection the printer
        // cannot finish, and shutdown must not hang on the user noticing. By this point the host
        // windows have been destroyed, so even a blocked join no longer holds Explorer's input
        // queue — the bound is belt and braces. Only dispose the collection if the printer really
        // did finish; disposing it out from under a thread still inside GetConsumingEnumerable
        // would raise an unhandled ObjectDisposedException on that thread and kill the process.
        if (_printer.Join(TimeSpan.FromSeconds(2)))
            _lines.Dispose();
    }
}
