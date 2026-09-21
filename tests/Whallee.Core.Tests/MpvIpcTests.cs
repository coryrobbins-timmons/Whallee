using System.Globalization;
using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class MpvIpcTests
{
    [Fact]
    public void ReadsThePositionOutOfMpvsReply() =>
        Assert.Equal(12.345, MpvIpc.ParseTimePos(
            "{\"data\":12.345,\"request_id\":7,\"error\":\"success\"}"));

    [Fact]
    public void ReadsAWholeNumberPosition() =>
        Assert.Equal(0d, MpvIpc.ParseTimePos(
            "{\"data\":0,\"request_id\":7,\"error\":\"success\"}"));

    /// <summary>
    /// A player that has not begun playing reports its position as JSON null. That is an answer, not
    /// a failure, and it must read as "no position" rather than as zero - reporting it as 0.00 would
    /// make a player that never started look perfectly aligned.
    /// </summary>
    [Fact]
    public void APositionOfJsonNullIsNoAnswer() =>
        Assert.Null(MpvIpc.ParseTimePos(
            "{\"data\":null,\"request_id\":7,\"error\":\"success\"}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"request_id\":7,\"error\":\"property unavailable\"}")]
    [InlineData("{\"event\":\"playback-restart\"}")]
    public void GivesNothingForARepyItCannotRead(string? reply) =>
        Assert.Null(MpvIpc.ParseTimePos(reply));

    /// <summary>
    /// THE SILENT-FAILURE CASE. mpv always emits a '.' decimal separator. Parsing with the current
    /// culture on a machine set to a comma would read every position as unparseable, and this would
    /// report "no answer" for every player while looking like the pipe was at fault.
    /// </summary>
    [Fact]
    public void ReadsThePositionOnACommaDecimalMachine()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(12.345, MpvIpc.ParseTimePos(
                "{\"data\":12.345,\"request_id\":7,\"error\":\"success\"}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void PauseCommandIsTheExactJsonMpvExpects() =>
        Assert.Equal("{\"command\":[\"set_property\",\"pause\",true]}",
                     MpvIpc.PauseCommand(true));

    [Fact]
    public void ResumeCommandIsTheExactJsonMpvExpects() =>
        Assert.Equal("{\"command\":[\"set_property\",\"pause\",false]}",
                     MpvIpc.PauseCommand(false));

    // THE BUG THIS GUARDS: interpolating a bool directly yields .NET's "True"/"False", which is not
    // valid JSON and which mpv rejects outright - and it would fail silently, because the pipe
    // write still succeeds. Assert the casing explicitly.
    [Fact]
    public void TheBooleanIsLowercaseJsonNotDotNetToString()
    {
        Assert.Contains("true", MpvIpc.PauseCommand(true));
        Assert.DoesNotContain("True", MpvIpc.PauseCommand(true));
        Assert.DoesNotContain("False", MpvIpc.PauseCommand(false));
    }

    // mpv reads its IPC as one JSON object per line, so the command itself must not contain a
    // newline - Send appends exactly one.
    [Fact]
    public void TheCommandCarriesNoNewlineOfItsOwn() =>
        Assert.DoesNotContain("\n", MpvIpc.PauseCommand(true));

    [Fact]
    public void SeekToStartIsTheExactJsonMpvExpects() =>
        Assert.Equal("{\"command\":[\"seek\",0,\"absolute\"]}", MpvIpc.SeekToStartCommand);

    // Same reason as the pause command above: mpv reads one JSON object per line and Send appends
    // exactly one newline.
    [Fact]
    public void TheSeekCommandCarriesNoNewlineOfItsOwn() =>
        Assert.DoesNotContain("\n", MpvIpc.SeekToStartCommand);
}
