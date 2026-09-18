using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class MpvIpcTests
{
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
