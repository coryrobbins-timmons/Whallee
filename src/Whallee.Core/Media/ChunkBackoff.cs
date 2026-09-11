namespace Whallee.Core.Media;

/// <summary>
/// How far to back off when the server refuses a ranged request.
/// </summary>
/// <remarks>
/// WHY BACK OFF AT ALL. This network answers a bounded "bytes=N-M" with 206 only while M sits
/// inside roughly the first minute of a video; past that it answers 403. The refusal depends on the
/// OFFSET, not the size - measured 2026-09-10, a 32 KiB read at 4.99 MiB is refused while a 2 MiB
/// read at 0 succeeds. So a chunk that straddles the wall fails ENTIRELY even though its opening
/// bytes were available, and the old fixed-size fetcher retried that same doomed range three times
/// and then stopped, keeping 33.6 s of a video whose first ~55 s were reachable.
///
/// The wall is a DURATION, not a byte count - see ImportPlanner.ContentCeiling, which measured the
/// same ~60 s limit across videos whose byte counts spanned more than 10x. Its byte position
/// therefore differs for every video, which is why this discovers the wall instead of assuming it.
///
/// MONOTONICALLY NON-INCREASING, deliberately. Once the server starts refusing, the distance left
/// to the wall is smaller than the chunk just refused, so growing back only spends a request to be
/// refused again.
/// </remarks>
public static class ChunkBackoff
{
    /// <summary>The size to start at, and the largest ever requested.</summary>
    public static readonly long InitialSize = 2 * 1024 * 1024;

    /// <summary>
    /// The smallest chunk worth asking for. At the bitrates in play this is about one second of
    /// video, so the fetch lands within a second of the wall; five halvings reach it.
    /// </summary>
    public static readonly long MinimumSize = 64 * 1024;

    /// <summary>
    /// The next size to try after <paramref name="currentSize"/> was refused, or null when the
    /// wall has been found and the fetch should stop.
    /// </summary>
    /// <remarks>
    /// NULL MEANS "STOP NORMALLY", not "error". Everything fetched before the wall is good, and the
    /// caller returns it.
    /// </remarks>
    public static long? NextSizeAfterRefusal(long currentSize)
    {
        // A refusal at (or below) the floor is the wall itself: there is no smaller request left to
        // distinguish "this chunk was too big" from "this offset is unreachable".
        if (currentSize <= MinimumSize) return null;

        var halved = currentSize / 2;

        // CLAMPED, NOT FLOORED THROUGH. Halving a size that is not a multiple of the floor can land
        // below it; returning that would fetch in ever-smaller slivers.
        return halved > MinimumSize ? halved : MinimumSize;
    }
}
