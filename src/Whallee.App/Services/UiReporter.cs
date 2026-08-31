using System.Collections.ObjectModel;
using System.Windows;
using Whallee.Core.Diagnostics;

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
public sealed class UiReporter : IDisposable
{
    private const int MaxMessages = 200;

    private readonly LogFileSink? _sink;

    public ObservableCollection<string> Messages { get; } = new();

    /// <summary>Where this session is being logged, or null if file logging is off.</summary>
    public string? LogPath => _sink?.FilePath;

    /// <param name="logPath">
    /// Log file to append to. Defaults to <see cref="LogFileSink.DefaultPath"/>; pass null to
    /// disable file logging entirely.
    /// </param>
    public UiReporter(string? logPath = null, bool logToFile = true)
    {
        if (!logToFile) return;
        _sink = new LogFileSink(logPath);
        _sink.Write($"=== session start — pid {Environment.ProcessId} ===");
    }

    public void Report(string message)
    {
        // FILE FIRST, and before the dispatcher check on purpose. Application.Current is null
        // during shutdown, so a UI-only reporter silently drops exactly the messages emitted while
        // the engine tears down — the ones most worth having. The sink only enqueues, so this is
        // still safe on the pump thread.
        _sink?.Write(message);

        // Null during shutdown, and before Application.Current is established. Dropping the
        // message for the UI is correct there: there is no UI left to show it to, and throwing
        // would violate the must-not-throw half of the callback contract.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(() =>
        {
            Messages.Add(message);
            while (Messages.Count > MaxMessages) Messages.RemoveAt(0);
        });
    }

    public void Dispose() => _sink?.Dispose();
}
