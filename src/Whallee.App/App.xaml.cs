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
    private Thread? _showListenerThread;

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

        // See EngineHost.AlignPassCompleted's remarks and PlaybackGovernor.ForceReassertNextTick's:
        // PlayerSync.AlignAsync resumes every player it touches unconditionally, including ones the
        // governor paused, so the governor must be told to re-check itself after every pass or a
        // pause it applied can be silently undone. This subscription is the only place either side
        // needs to know about the other; Whallee.Core still knows nothing of PlaybackGovernor.
        Engine.AlignPassCompleted += _governor.ForceReassertNextTick;

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
        //
        // THE WHOLE BODY IS WRAPPED IN TRY/CATCH, and this is defense in depth rather than the
        // primary fix. OnExit joins this thread (see below) before disposing _showRequest, which is
        // what should normally prevent the race outright - but a join with a bounded timeout is not
        // a guarantee, and Dispatcher.BeginInvoke after the Dispatcher itself has begun shutting
        // down can throw an InvalidOperationException that joining alone cannot rule out (shutdown
        // order is Dispatcher-driven, not something this thread controls). Without this catch,
        // either exception - ObjectDisposedException from a WaitOne on a disposed handle, or the
        // Dispatcher case - would escape unhandled on a background thread and crash the whole
        // process on the way out, which is a strictly worse outcome than a missed show request
        // during shutdown.
        var listener = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    // Bounded, so shutdown does not depend on the event ever being signalled.
                    if (!handle.WaitOne(TimeSpan.FromMilliseconds(500))) continue;
                    if (stop.IsCancellationRequested) return;

                    Dispatcher.BeginInvoke(show);
                }
            }
            catch (Exception)
            {
                // Swallowed. This thread's only job past this point is to end quietly - the app is
                // exiting either way, and a show request lost in the last half-second of shutdown is
                // never worse than a process crash.
            }
        })
        {
            IsBackground = true,
            Name = "Whallee.ShowRequestListener"
        };
        listener.Start();
        _showListenerThread = listener;
    }

    /// <summary>The only way out of the app, since closing the window merely hides it.</summary>
    public void ExitApplication()
    {
        IsShuttingDown = true;
        Shutdown();
    }

    /// <summary>
    /// Raises a Windows notification, if there is a tray icon to raise it from.
    /// </summary>
    /// <remarks>
    /// Goes through App because _tray is private and the tray is created after the window: a page
    /// cannot hold a reference to it safely. A null tray is a normal state during startup and
    /// shutdown, not an error.
    /// </remarks>
    public void Notify(string title, string message, bool failed) =>
        _tray?.Notify(title, message, failed);

    protected override void OnExit(ExitEventArgs e)
    {
        // Listener first: it marshals onto the Dispatcher, and letting it fire while the window and
        // tray are being disposed invites a show request into a half-torn-down app.
        //
        // JOINED BEFORE _showRequest IS DISPOSED, AND THAT ORDER IS THE FIX. The listener thread's
        // loop checks stop.IsCancellationRequested and then calls handle.WaitOne - and Cancel() alone
        // only sets the flag the loop checks BETWEEN those two steps. Without joining, a thread that
        // has just passed the check can still be about to call WaitOne on _showRequest the instant
        // after this method disposes it, which throws an unhandled ObjectDisposedException on a
        // background thread and takes the whole process down with it. A short, bounded join gives the
        // thread its one remaining WaitOne timeout (500 ms) to notice the cancellation and return
        // before the handle underneath it goes away; the listener's own try/catch is the second layer
        // for whatever this bound does not catch (see the thread's remarks at OnStartup).
        _showListener?.Cancel();
        _showListenerThread?.Join(TimeSpan.FromSeconds(2));
        _showListenerThread = null;
        _showListener?.Dispose();
        _showListener = null;
        _showRequest?.Dispose();
        _showRequest = null;

        // Tray first: it owns a message window, and leaving a live icon behind while the engine
        // tears down invites a menu click into a half-disposed app.
        _tray?.Dispose();

        // Unsubscribed before either side is disposed - a defunct governor has no business being
        // told to re-check itself, and there is no pass left to complete once teardown has started.
        if (_governor is not null && Engine is not null)
            Engine.AlignPassCompleted -= _governor.ForceReassertNextTick;

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
