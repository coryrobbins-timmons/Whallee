using System.Windows;
using Whallee.App.Services;

namespace Whallee.App;

public partial class App : Application
{
    public UiReporter Reporter { get; } = new();
    public EngineHost? Engine { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Engine = new EngineHost(Reporter);

        // Started BEFORE the window is shown, and deliberately on this thread - see EngineHost's
        // remarks for why the host windows must belong to the pumping Dispatcher thread. Starting
        // first also means the initial Start() cannot re-enter XAML: there is no window bound to
        // Reporter.Messages yet, so the messages it queues simply accumulate and appear once
        // MainWindow binds to the collection.
        Engine.Start();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Engine?.Dispose();
        base.OnExit(e);
    }
}
