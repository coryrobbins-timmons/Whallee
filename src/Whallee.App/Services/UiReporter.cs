using System.Collections.ObjectModel;
using System.Windows;

namespace Whallee.App.Services;

/// <summary>
/// Collects engine status and error strings for display. Replaces the harness's ConsoleReporter.
/// </summary>
/// <remarks>
/// Report() is safe to call from any thread, but the collection is only ever mutated on the UI
/// thread, because WPF forbids cross-thread collection changes on a bound ObservableCollection.
/// InvokeAsync (not Invoke) is deliberate: Report is handed to WallpaperEngine as its whole-pass
/// failure callback and to ShellWatcher as its error callback, both of which fire ON THE PUMP
/// THREAD from inside a dispatched message. That callback contract says enqueue only - never
/// block, never throw. A synchronous Invoke from the pump thread during a nested pump is a
/// self-deadlock; InvokeAsync only queues, which is exactly what the contract asks for.
/// </remarks>
public sealed class UiReporter
{
    private const int MaxMessages = 200;

    public ObservableCollection<string> Messages { get; } = new();

    public void Report(string message)
    {
        // Null during shutdown, and before Application.Current is established. Dropping the
        // message is correct: there is no UI left to show it to, and throwing here would violate
        // the must-not-throw half of the callback contract.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(() =>
        {
            Messages.Add(message);
            while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
        });
    }
}
