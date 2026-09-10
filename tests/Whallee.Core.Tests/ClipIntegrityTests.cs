using Whallee.Core.Media;

namespace Whallee.Core.Tests;

public class ClipIntegrityTests
{
    [Fact]
    public void ExitZeroWithADurationIsReadable()
    {
        var check = ClipIntegrity.Interpret(0, "33.620000\n", "");
        Assert.Equal(ClipReadability.Readable, check.Verdict);
    }

    /// <summary>
    /// The failure this whole class was written for: ffprobe running and rejecting the file.
    /// </summary>
    [Fact]
    public void NonZeroExitIsUnreadableAndCarriesFfprobesReason()
    {
        var check = ClipIntegrity.Interpret(
            1, "", "[mov,mp4,m4a,3gp,3g2,mj2 @ 000001] moov atom not found");

        Assert.Equal(ClipReadability.Unreadable, check.Verdict);
        Assert.Contains("moov atom not found", check.Detail);
    }

    [Fact]
    public void NonZeroExitWithNoStderrStillReportsTheCode()
    {
        var check = ClipIntegrity.Interpret(3, "", "");
        Assert.Equal(ClipReadability.Unreadable, check.Verdict);
        Assert.Contains("3", check.Detail);
    }

    /// <summary>
    /// THE REGRESSION THIS CLASS EXISTS TO PREVENT. The previous implementation treated "no
    /// duration" as a broken file, which meant a probe that told us nothing failed the import. Not
    /// knowing is not the same as knowing it is broken.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("N/A\n")]
    [InlineData("0.000000\n")]
    public void ExitZeroWithoutAUsableDurationIsUnknownNotUnreadable(string stdout)
    {
        var check = ClipIntegrity.Interpret(0, stdout, "");
        Assert.Equal(ClipReadability.Unknown, check.Verdict);
    }

    [Fact]
    public void NullStreamsAreToleratedRatherThanThrowing()
    {
        Assert.Equal(ClipReadability.Unknown, ClipIntegrity.Interpret(0, null, null).Verdict);
        Assert.Equal(ClipReadability.Unreadable, ClipIntegrity.Interpret(1, null, null).Verdict);
    }

    /// <summary>
    /// ffprobe always prints a dot, whatever the machine locale is - same reasoning as MediaProbe.
    /// </summary>
    [Fact]
    public void ParsesTheDurationWithAnInvariantDecimalPoint()
    {
        var check = ClipIntegrity.Interpret(0, "54.125000\n", "");
        Assert.Equal(ClipReadability.Readable, check.Verdict);
        // Asserted to one decimal on purpose: the second is a rounding-mode coin flip on an
        // exactly-representable .125, and pinning it would test the framework, not this code.
        Assert.StartsWith("54.1", check.Detail);
    }
}
