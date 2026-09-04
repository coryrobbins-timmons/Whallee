using System.Windows;
using Whallee.App.Services;
using Whallee.App.Tray;

namespace Whallee.App;

public partial class App : Application
{
    private Mutex? _instanceLock;
    private TrayIcon? _tray;
    private PlaybackGovernor? _governor;

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
            Reporter.Report("another copy is already running — exiting");

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
    }

    /// <summary>The only way out of the app, since closing the window merely hides it.</summary>
    public void ExitApplication()
    {
        IsShuttingDown = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
