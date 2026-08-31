using System.Collections.Concurrent;
using System.Text;

namespace Whallee.Core.Diagnostics;

/// <summary>
/// Appends diagnostic lines to a file from a background thread.
/// </summary>
/// <remarks>
/// WHY A THREAD AND NOT JUST File.AppendAllText: Write() is reachable from the pump thread — the
/// engine's onStartFailure callback and ShellWatcher's onError both fire there, inside a WndProc.
/// That thread owns host windows whose input queue is attached to Explorer's desktop, so anything
/// blocking on it freezes the USER'S desktop. Synchronous disk I/O on a stalled or cloud-backed
/// path is exactly that. Write() therefore only enqueues; a background thread does the I/O.
/// This mirrors the harness's ConsoleReporter, which exists for the same reason.
///
/// The file is opened FileShare.ReadWrite with AutoFlush, deliberately: the log's whole purpose is
/// to be readable WHILE the app is running, so a verification step does not have to be caught live.
/// </remarks>
public sealed class LogFileSink : IDisposable
{
    public const long DefaultMaxBytes = 1024 * 1024;

    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _writer;
    private bool _disposed;

    public string FilePath { get; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whallee", "whallee.log");

    /// <summary>Previous log kept alongside the current one, so a rotation never destroys the only copy.</summary>
    public static string RotatedPathFor(string path) => path + ".1";

    public static bool ShouldRotate(long currentBytes, long maxBytes) =>
        maxBytes > 0 && currentBytes >= maxBytes;

    /// <summary>
    /// Timestamped with the CALLER's clock rather than the drain thread's, so a line's time is when
    /// the event happened, not when the queue got to it. That distinction is the difference between
    /// a log that can be correlated against the Windows event log and one that cannot.
    /// </summary>
    public static string FormatLine(DateTimeOffset timestamp, string message) =>
        $"{timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss.fff}  {message}";

    public LogFileSink(string? path = null, long maxBytes = DefaultMaxBytes)
    {
        FilePath = path ?? DefaultPath;
        Rotate(FilePath, maxBytes);

        _writer = new Thread(Drain) { IsBackground = true, Name = "whallee-log" };
        _writer.Start();
    }

    /// <summary>Queues one line. Safe from a WndProc: never touches the disk, never blocks, never throws.</summary>
    public void Write(string message)
    {
        try
        {
            _lines.Add(FormatLine(DateTimeOffset.Now, message));
        }
        // Both shutdown races: InvalidOperationException once adding is completed, and
        // ObjectDisposedException (which derives from it) once the collection is gone.
        catch (InvalidOperationException) { }
    }

    private static void Rotate(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && ShouldRotate(info.Length, maxBytes))
                File.Move(path, RotatedPathFor(path), overwrite: true);
        }
        catch (Exception)
        {
            // A log that cannot rotate must not stop the app from starting.
        }
    }

    private void Drain()
    {
        StreamWriter? writer = null;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // FileShare.ReadWrite so this file can be read while the app holds it open; AutoFlush
            // so a crash still leaves every line already written on disk. A buffered writer would
            // lose precisely the lines that explain the crash.
            writer = new StreamWriter(
                new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception)
        {
            // Keep draining with no writer rather than returning: a producer must never be left
            // blocked on a queue nobody is consuming.
        }

        foreach (var line in _lines.GetConsumingEnumerable())
        {
            try
            {
                writer?.WriteLine(line);
            }
            catch (Exception)
            {
                // EVERYTHING, matching ConsoleReporter's reasoning: this is a background thread,
                // where an unhandled exception terminates the whole process — and it would do so
                // during teardown, with mpv processes mid-kill. Dropping a diagnostic line is
                // always the better outcome than killing the app over one.
            }
        }

        try { writer?.Dispose(); } catch (Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lines.CompleteAdding();

        // Bounded, and the collection is disposed only if the thread really finished — disposing it
        // out from under a thread still inside GetConsumingEnumerable would raise an unhandled
        // ObjectDisposedException there and kill the process during shutdown.
        if (_writer.Join(TimeSpan.FromSeconds(2)))
            _lines.Dispose();
    }
}
