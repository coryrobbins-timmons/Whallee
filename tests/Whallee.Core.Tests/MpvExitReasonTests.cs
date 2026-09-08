using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class MpvExitReasonTests
{
    private const string Clip = @"C:\clips\City Skyline.mp4";

    [Fact]
    public void ExplainsAnInitialisationFailure()
    {
        Assert.Equal($"mpv failed to initialise ({Clip})", MpvExitReason.Describe(1, Clip));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TreatsBothPlaybackCodesTheSame(int exitCode)
    {
        Assert.Equal($"mpv could not play the file ({Clip})", MpvExitReason.Describe(exitCode, Clip));
    }

    /// <summary>
    /// A clean exit is still a failure from the wallpaper's point of view: the display is not
    /// playing anything. Reporting nothing here would leave a black monitor with no explanation.
    /// </summary>
    [Fact]
    public void TreatsACleanImmediateExitAsAFailure()
    {
        Assert.Equal($"mpv exited immediately ({Clip})", MpvExitReason.Describe(0, Clip));
    }

    /// <summary>
    /// An unrecognised code is reported verbatim rather than guessed at - a confident wrong
    /// explanation sends the reader somewhere else entirely.
    /// </summary>
    [Fact]
    public void ReportsAnUnknownCodeVerbatim()
    {
        Assert.Equal($"mpv exited with code 137 ({Clip})", MpvExitReason.Describe(137, Clip));
    }

    [Fact]
    public void AlwaysNamesTheClip()
    {
        foreach (var code in new[] { 0, 1, 2, 3, 42 })
            Assert.Contains(Clip, MpvExitReason.Describe(code, Clip));
    }
}
