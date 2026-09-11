using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class ChunkBackoffTests
{
    /// <summary>
    /// THE CASE THIS EXISTS FOR, measured 2026-09-10. A 2 MiB chunk at offset 2 MiB was refused
    /// because it ENDED past the network's wall, not because its start was unreachable - a 1 MiB
    /// chunk at the same offset would have been served. The old code retried the same size three
    /// times and stopped, losing ~22 seconds of video.
    /// </summary>
    [Fact]
    public void AFullSizeRefusalHalves()
    {
        // The L suffix matters: NextSizeAfterRefusal returns long?, and xUnit boxes both sides, so
        // an int literal here compares unequal to an equal long and the test fails for no reason.
        Assert.Equal(1_048_576L, ChunkBackoff.NextSizeAfterRefusal(ChunkBackoff.InitialSize));
    }

    /// <summary>
    /// Successive refusals converge on the wall rather than stalling at one size.
    /// </summary>
    [Fact]
    public void SuccessiveRefusalsWalkDownToTheFloor()
    {
        var sizes = new List<long>();
        long? size = ChunkBackoff.InitialSize;

        while (size is { } current)
        {
            sizes.Add(current);
            size = ChunkBackoff.NextSizeAfterRefusal(current);
        }

        Assert.Equal(
            new long[] { 2097152, 1048576, 524288, 262144, 131072, 65536 },
            sizes);
    }

    /// <summary>
    /// A refusal at the floor is the wall. Null means stop, and is what ends the fetch loop
    /// normally rather than as an error.
    /// </summary>
    [Fact]
    public void ARefusalAtTheFloorFindsTheWall()
    {
        Assert.Null(ChunkBackoff.NextSizeAfterRefusal(ChunkBackoff.MinimumSize));
    }

    [Fact]
    public void ASizeBelowTheFloorAlsoStops()
    {
        Assert.Null(ChunkBackoff.NextSizeAfterRefusal(1024));
    }

    /// <summary>
    /// Defensive: a non-positive size can only come from a caller bug, and returning a smaller
    /// number would loop forever.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveSizeStops(long size)
    {
        Assert.Null(ChunkBackoff.NextSizeAfterRefusal(size));
    }

    /// <summary>
    /// CLAMPED TO THE FLOOR, NOT UNDERSHOOTING IT. Halving a size that is not a power of two can
    /// land below the floor; returning that value would fetch in slivers and, worse, could return
    /// a size arbitrarily close to zero.
    /// </summary>
    [Fact]
    public void HalvingBelowTheFloorClampsToIt()
    {
        Assert.Equal<long?>(ChunkBackoff.MinimumSize, ChunkBackoff.NextSizeAfterRefusal(100_000));
    }

    /// <summary>
    /// The walk in SuccessiveRefusalsWalkDownToTheFloor only terminates if the floor is reachable
    /// by halving. Pinning that relationship stops a future edit to either constant from silently
    /// making the loop crawl or never converge.
    /// </summary>
    [Fact]
    public void TheFloorIsReachableByHalvingFromTheInitialSize()
    {
        Assert.True(ChunkBackoff.MinimumSize < ChunkBackoff.InitialSize);
        Assert.Equal(0L, ChunkBackoff.InitialSize % ChunkBackoff.MinimumSize);
    }
}
