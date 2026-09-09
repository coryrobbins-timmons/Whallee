using System.Diagnostics;

namespace Whallee.Core;

/// <summary>
/// Stage timings for one start or swap pass, for the log.
/// </summary>
/// <remarks>
/// EXISTS BECAUSE "APPLYING A CLIP TAKES ABOUT THREE SECONDS" WAS UNATTRIBUTABLE. The engine's own
/// log recorded when a pass began and nothing else, so every candidate — mpv's liveness window, the
/// OneDrive-backed library listing, mpv's own start-up, tearing the old generation down — was
/// equally plausible and none was measurable. Guessing at that is how you optimise the wrong one.
///
/// Deliberately not an <c>#if DEBUG</c>: the interesting numbers here depend on this machine's
/// OneDrive folder and this machine's clips, so they have to be measurable in the copy that is
/// actually installed. The cost is a Stopwatch and one log line per pass.
/// </remarks>
public sealed class PassProfile
{
    private readonly List<(string Stage, long Ms)> _stages = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();

    /// <summary>Times <paramref name="work"/> and records it under <paramref name="stage"/>.</summary>
    public void Measure(string stage, Action work)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            work();
        }
        finally
        {
            watch.Stop();
            _stages.Add((stage, watch.ElapsedMilliseconds));
        }
    }

    /// <summary>Records a stage that was timed by the caller.</summary>
    public void Record(string stage, long ms) => _stages.Add((stage, ms));

    /// <summary>
    /// A single line: the total, then each stage in the order it ran.
    /// </summary>
    /// <remarks>
    /// Stages are listed even when they cost 0 ms. A stage that is reliably free is itself worth
    /// knowing — it is how you rule a suspect out rather than merely fail to notice it.
    /// </remarks>
    public string Describe(string kind)
    {
        var parts = string.Join(", ", _stages.Select(s => $"{s.Stage} {s.Ms}"));
        return $"{kind} pass {_total.ElapsedMilliseconds} ms ({parts})";
    }
}
