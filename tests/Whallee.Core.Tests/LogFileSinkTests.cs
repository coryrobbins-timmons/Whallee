using System.Text;
using Whallee.Core.Diagnostics;

namespace Whallee.Core.Tests;

public class LogFileSinkTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "whallee-log-tests-" + Guid.NewGuid().ToString("N"));

    private string PathIn(string name) => Path.Combine(_dir, name);

    public LogFileSinkTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FormatLine_PrefixesSortableTimestamp()
    {
        var at = new DateTimeOffset(2026, 8, 31, 13, 52, 47, 123, TimeSpan.Zero);

        var line = LogFileSink.FormatLine(at, "hello");

        // Local time, so assert the shape and the payload rather than a fixed hour.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  hello$", line);
    }

    [Fact]
    public void FormatLine_SortsChronologicallyAsText()
    {
        var earlier = LogFileSink.FormatLine(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero), "a");
        var later = LogFileSink.FormatLine(new DateTimeOffset(2026, 11, 12, 13, 14, 15, 16, TimeSpan.Zero), "b");

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Theory]
    [InlineData(0, 100, false)]
    [InlineData(99, 100, false)]
    [InlineData(100, 100, true)]
    [InlineData(101, 100, true)]
    public void ShouldRotate_TripsAtOrAboveTheCap(long size, long max, bool expected) =>
        Assert.Equal(expected, LogFileSink.ShouldRotate(size, max));

    [Fact]
    public void ShouldRotate_ZeroCapMeansNeverRotate() =>
        Assert.False(LogFileSink.ShouldRotate(long.MaxValue, 0));

    [Fact]
    public void DefaultPath_LivesBesideTheConfig()
    {
        var path = LogFileSink.DefaultPath;

        Assert.EndsWith(Path.Combine("Whallee", "whallee.log"), path);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), path);
    }

    [Fact]
    public void RotatedPathFor_KeepsThePreviousLogAlongside() =>
        Assert.Equal(@"C:\x\whallee.log.1", LogFileSink.RotatedPathFor(@"C:\x\whallee.log"));

    [Fact]
    public void Write_AppendsEveryLineInOrder()
    {
        var path = PathIn("whallee.log");

        using (var sink = new LogFileSink(path))
        {
            sink.Write("first");
            sink.Write("second");
            sink.Write("third");
        }   // Dispose drains and joins the writer thread.

        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("first", lines[0]);
        Assert.EndsWith("second", lines[1]);
        Assert.EndsWith("third", lines[2]);
    }

    [Fact]
    public void Write_IsReadableWhileTheSinkIsStillOpen()
    {
        // The whole point of the sink: a verification step must be able to read the log without
        // waiting for the app to exit.
        var path = PathIn("whallee.log");
        using var sink = new LogFileSink(path);

        sink.Write("live line");
        SpinWait.SpinUntil(() => File.Exists(path) && ReadShared(path).Contains("live line"),
                           TimeSpan.FromSeconds(5));

        Assert.Contains("live line", ReadShared(path));
    }

    [Fact]
    public void Write_AfterDispose_IsIgnoredRatherThanThrowing()
    {
        var path = PathIn("whallee.log");
        var sink = new LogFileSink(path);
        sink.Dispose();

        // The pump-thread callbacks must never see an exception out of a log write.
        var ex = Record.Exception(() => sink.Write("late"));

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sink = new LogFileSink(PathIn("whallee.log"));
        sink.Dispose();

        Assert.Null(Record.Exception(() => sink.Dispose()));
    }

    [Fact]
    public void Construction_RotatesAnOversizedLogInsteadOfGrowingIt()
    {
        var path = PathIn("whallee.log");
        File.WriteAllText(path, new string('x', 500));

        using (var sink = new LogFileSink(path, maxBytes: 100))
            sink.Write("fresh");

        Assert.Equal(500, new FileInfo(LogFileSink.RotatedPathFor(path)).Length);
        Assert.EndsWith("fresh", File.ReadAllLines(path).Single());
    }

    [Fact]
    public void Construction_KeepsASmallLogAndAppendsToIt()
    {
        var path = PathIn("whallee.log");
        File.WriteAllText(path, "kept" + Environment.NewLine);

        using (var sink = new LogFileSink(path, maxBytes: LogFileSink.DefaultMaxBytes))
            sink.Write("added");

        var lines = File.ReadAllLines(path);
        Assert.Equal("kept", lines[0]);
        Assert.EndsWith("added", lines[1]);
        Assert.False(File.Exists(LogFileSink.RotatedPathFor(path)));
    }

    [Fact]
    public void Construction_OnAnUnwritablePathDoesNotThrow()
    {
        // A log that cannot be opened must never stop the app starting. The directory below is a
        // file, so creating it as a directory fails.
        var blocker = PathIn("blocker");
        File.WriteAllText(blocker, "x");

        var ex = Record.Exception(() =>
        {
            using var sink = new LogFileSink(Path.Combine(blocker, "whallee.log"));
            sink.Write("dropped");
        });

        Assert.Null(ex);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
