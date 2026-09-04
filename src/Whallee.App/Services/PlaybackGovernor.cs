using System.Runtime.InteropServices;
using System.Windows.Threading;
using Whallee.Core.Playback;

namespace Whallee.App.Services;

/// <summary>
/// Pauses and resumes wallpaper playback according to the Performance rules.
/// </summary>
/// <remarks>
/// ACTS ONLY ON A TRANSITION. At one tick a second, applying the verdict every time would mean
/// constant pipe traffic and a log line per second; this compares the desired state against what
/// was last applied and does nothing when they agree.
///
/// RE-ASSERTS AFTER AN ENGINE RESTART, which is the subtle half. A restart disposes every player
/// and starts fresh ones - and fresh mpv processes start UNPAUSED. Tracking which pipes the current
/// state was applied to means the next tick notices the players changed and re-applies, without
/// coupling this class to Restart(). Self-healing within a second, and it covers every path that
/// creates new players, including ones that do not exist yet.
/// </remarks>
public sealed class PlaybackGovernor : IDisposable
{
    /// <summary>
    /// One second, chosen over two on the owner's decision 2026-09-04.
    /// </summary>
    /// <remarks>
    /// The noticeable half is RESUMING: a frozen wallpaper after alt-tabbing out of a fullscreen app
    /// is visible, whereas a second of playback behind that app is not. The check itself is two
    /// cheap system calls, so halving the interval costs nothing measurable.
    /// </remarks>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private readonly EngineHost _engine;
    private readonly UiReporter _reporter;
    private readonly DispatcherTimer _timer;

    private bool _paused;
    private string[] _appliedTo = Array.Empty<string>();
    private bool _disposed;
    private bool _ticking;

    public PlaybackGovernor(EngineHost engine, UiReporter reporter)
    {
        _engine = engine;
        _reporter = reporter;

        // A DispatcherTimer ticks on the UI thread, which is correct: the tick itself only makes
        // two cheap system calls. The one piece of I/O - the pipe writes - is pushed off it below.
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    /// async void, and wrapped end to end for it.
    /// </summary>
    /// <remarks>
    /// THIS ONE MATTERS MORE THAN MOST. A DispatcherTimer tick is a Dispatcher callback, so an
    /// escaping exception terminates the process - and this fires every second, which would turn
    /// any transient fault into an immediate crash rather than a rare one.
    /// </remarks>
    private async void OnTick(object? sender, EventArgs e)
    {
        // RE-ENTRANCY GUARD. async void returns to the dispatcher at the first await, so the next
        // tick fires while ApplyAsync is still awaiting its pipe writes - with _paused and
        // _appliedTo not yet updated, which would re-apply and log a second time. Three unreachable
        // players at a 500 ms connect timeout each is 1.5 s against a 1 s tick, so the overlap is
        // reachable, not theoretical. Skipping is right rather than queueing: the next tick is a
        // second away and re-evaluates from scratch.
        if (_ticking) return;
        _ticking = true;

        try
        {
            var config = _engine.Config;
            var pipes = _engine.ActivePlayers.Select(p => p.Player.PipeName).ToArray();

            if (!config.PauseOnFullscreen && !config.PauseOnBattery)
            {
                // Both rules off. Resume once if we had paused - turning the feature off must never
                // leave the wallpaper frozen - then stop doing anything.
                if (_paused) await ApplyAsync(false, pipes);
                return;
            }

            var shouldPause = PauseDecision.ShouldPause(
                QueryState(), OnBattery(), config.PauseOnFullscreen, config.PauseOnBattery);

            var playersChanged = !pipes.SequenceEqual(_appliedTo, StringComparer.Ordinal);

            if (shouldPause == _paused && !playersChanged) return;

            // The players changed but the verdict did not, and we want them PLAYING: fresh mpv
            // processes already are. Record the new pipes and send nothing.
            if (shouldPause == _paused && !shouldPause)
            {
                _appliedTo = pipes;
                return;
            }

            await ApplyAsync(shouldPause, pipes);
        }
        catch (Exception ex)
        {
            _reporter.Report($"pause check failed: {ex.Message}");
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <remarks>
    /// Task.Run because connecting to a named pipe is I/O and this thread owns windows parented
    /// into Explorer's desktop.
    ///
    /// LOGGED ONCE PER TRANSITION, NEVER PER TICK. At one tick a second a per-tick line would bury
    /// every other message within minutes and make the log useless for the thing it exists for.
    /// </remarks>
    private async Task ApplyAsync(bool pause, string[] pipes)
    {
        var command = MpvIpc.PauseCommand(pause);
        var reached = await Task.Run(() => pipes.Count(p => MpvIpc.Send(p, command)));

        _paused = pause;
        _appliedTo = pipes;

        _reporter.Report(pause
            ? $"paused playback on {reached}/{pipes.Length} display(s)"
            : $"resumed playback on {reached}/{pipes.Length} display(s)");
    }

    /// <remarks>
    /// A failed query returns AcceptsNotifications - "an ordinary desktop" - so a broken call means
    /// the wallpaper keeps playing rather than freezing. Fail safe toward playing.
    /// </remarks>
    private static UserNotificationState QueryState() =>
        SHQueryUserNotificationState(out var state) == 0
            ? state
            : UserNotificationState.AcceptsNotifications;

    /// <remarks>
    /// ACLineStatus is 0 offline, 1 online, 255 unknown - so only an explicit 0 counts as battery,
    /// and both "unknown" and a failed call mean mains. Fail safe toward playing.
    /// </remarks>
    private static bool OnBattery() =>
        GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stopped, but playback is NOT resumed. Dispose runs on the way out of the app, where the
        // mpv processes are about to be killed anyway - sending a resume they will never act on
        // would just risk a half-second stall in the teardown path.
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
