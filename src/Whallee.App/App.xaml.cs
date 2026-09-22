using System.Windows;
using Whallee.App.Services;
using Whallee.App.Tray;

namespace Whallee.App;

public partial class App : Application
{
    private Mutex? _instanceLock;
    private TrayIcon? _tray;
    private PlaybackGovernor? _governor;
    private EventWaitHandle? _showRequest;
    private CancellationTokenSource? _showListener;

    /// <summary>
    /// The event a second copy signals to ask the running copy to show itself.
    /// </summary>
    /// <remarks>
    /// Paired with the "Whallee.SingleInstance" mutex below and deliberately named in the same
    /// style. Both are unqualified names, so both are per-session - which is correct: a second copy
    /// in another Windows session would have its own mutex and should start its own instance.
    /// </remarks>
    private const string ShowRequestName = "Whallee.ShowRequest";

    public UiReporter Reporter { get; } = new();
    public EngineHost? Engine { get; private set; }

    /// <summary>
    /// True once the tray's Exit has been chosen. MainWindow reads it to tell a real shutdown from
    /// a close-to-hide, because with ShutdownMode=OnExplicitShutdown the window never closes on its
    /// own and an unconditional Cancel in OnClosing would make the app impossible to quit.
    /// </summary>
    public bool IsShuttingDown { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second instance would attach its own host windows to the same three displays: doubled
        // video layers, and two engines racing over the same config file.
        _instanceLock = new Mutex(initiallyOwned: true, "Whallee.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // SIGNAL THE RUNNING COPY BEFORE EXITING. This is the whole fix: the first instance
            // waits on this event and brings its window to the front, so the shortcut behaves the
            // way every other single-instance tray app does.
            //
            // Best effort. If the event cannot be opened - an older first instance that does not
            // create it - this falls back to exactly the previous behaviour, which is to exit
            // quietly. Never worth a crash dialog on a path whose job is to disappear.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowRequestName, out var running))
                {
                    using (running) running.Set();
                    Reporter.Report("another copy is already running — asked it to show itself");
                }
                else
                {
                    Reporter.Report("another copy is already running — exiting");
                }
            }
            catch (Exception)
            {
                Reporter.Report("another copy is already running — exiting");
            }

            // Dropped rather than released. This process never OWNED the mutex, and ReleaseMutex on
            // an unowned mutex throws ApplicationException - which would turn a clean "second copy
            // exits quietly" into a crash dialog on the way out.
            _instanceLock.Dispose();
            _instanceLock = null;

            Shutdown();
            return;
        }

        Engine = new EngineHost(Reporter);

        // Started BEFORE the window is shown, and deliberately on this thread - see EngineHost's
        // remarks for why the host windows must belong to the pumping Dispatcher thread. Starting
        // first also means the initial Start() cannot re-enter XAML: there is no window bound to
        // Reporter.Messages yet, so the messages it queues simply accumulate and appear once
        // MainWindow binds to the collection.
        Engine.Start();

        // AFTER Start(), so its first tick finds real players. Before the window, because it needs
        // nothing from the UI - it only reads config and the engine's player list.
        _governor = new PlaybackGovernor(Engine, Reporter);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _tray = new TrayIcon(
            onShow: () =>
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            },
            onExit: ExitApplication);

        // A SECOND COPY MUST NOT DIE SILENTLY. Before this, launching Whallee while it was already
        // running exited with no window, no message and no focus handed over, so the shortcut and
        // the taskbar pin both appeared to do nothing at all. The owner clicked nine times in 33
        // seconds on 2026-09-16 and got nothing back.
        //
        // A NAMED EVENT rather than a pipe or a window message: this needs to carry no data, only
        // "someone tried to start me", and an auto-reset event is the smallest thing that does that.
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName);
        _showListener = new CancellationTokenSource();

        var stop = _showListener.Token;
        var handle = _showRequest;
        var show = () =>
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        };

        // A BACKGROUND THREAD THAT BLOCKS, WHICH IS FINE ONLY BECAUSE IT IS NOT THE PASS THREAD.
        // WaitOne blocks outright; doing that on the Dispatcher thread would freeze the desktop,
        // since that thread owns windows re-parented into Explorer's desktop. The callback is
        // marshalled back with BeginInvoke, which does not block this thread either.
        var listener = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                // Bounded, so shutdown does not depend on the event ever being signalled.
                if (!handle.WaitOne(TimeSpan.FromMilliseconds(500))) continue;
                if (stop.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(show);
            }
        })
        {
            IsBackground = true,
            Name = "Whallee.ShowRequestListener"
        };
        listener.Start();
    }

    /// <summary>The only way out of the app, since closing the window merely hides it.</summary>
    public void ExitApplication()
    {
        IsShuttingDown = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Listener first: it marshals onto the Dispatcher, and letting it fire while the window and
        // tray are being disposed invites a show request into a half-torn-down app.
        _showListener?.Cancel();
        _showListener?.Dispose();
        _showListener = null;
        _showRequest?.Dispose();
        _showRequest = null;

        // Tray first: it owns a message window, and leaving a live icon behind while the engine
        // tears down invites a menu click into a half-disposed app.
        _tray?.Dispose();

        // Governor before the engine. It ticks every second and reaches into the engine's player
        // list; leaving it running while the engine tears down invites a tick against players that
        // are being disposed underneath it.
        _governor?.Dispose();

        // Engine before reporter. Disposing the engine emits teardown messages, and those are the
        // ones a post-mortem most wants; flushing the log first would throw them away.
        Engine?.Dispose();

        // Written last, so the log distinguishes a clean exit from a process that died: a session
        // with no end line ended badly, and that is worth knowing without digging in the event log.
        Reporter.Report("=== session end ===");
        Reporter.Dispose();

        // Null for a second instance, which never owned it - see OnStartup.
        if (_instanceLock is not null)
        {
            _instanceLock.ReleaseMutex();
            _instanceLock.Dispose();
            _instanceLock = null;
        }

        base.OnExit(e);
    }
}
