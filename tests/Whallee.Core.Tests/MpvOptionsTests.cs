using Whallee.Core.Configuration;
using Whallee.Core.Playback;

namespace Whallee.Core.Tests;

public class MpvOptionsTests
{
    private static IReadOnlyList<string> Build(ScalingMode scaling) =>
        MpvOptions.Build(0x1234, @"C:\a folder\clip.mp4", scaling, "whallee-0");

    [Fact]
    public void AlwaysMutes()
    {
        Assert.Contains("--volume=0", Build(ScalingMode.Fill));
    }

    [Fact]
    public void TargetsTheHostWindow()
    {
        Assert.Contains("--wid=4660", Build(ScalingMode.Fill)); // 0x1234 == 4660
    }

    [Fact]
    public void LoopsForever()
    {
        var args = Build(ScalingMode.Fill);
        Assert.Contains("--loop-file=inf", args);
        Assert.Contains("--keep-open=yes", args);
    }

    [Fact]
    public void PassesVideoPathAsFinalArgumentUnquoted()
    {
        var args = Build(ScalingMode.Fill);
        Assert.Equal(@"C:\a folder\clip.mp4", args[^1]);
    }

    [Fact]
    public void NamesTheIpcPipe()
    {
        Assert.Contains(@"--input-ipc-server=\\.\pipe\whallee-0", Build(ScalingMode.Fill));
    }

    [Theory]
    [InlineData(ScalingMode.Fill,     "--keepaspect=no")]
    [InlineData(ScalingMode.Fit,      "--panscan=0.0")]
    [InlineData(ScalingMode.FillCrop, "--panscan=1.0")]
    public void MapsScalingMode(ScalingMode mode, string expected)
    {
        Assert.Contains(expected, Build(mode));
    }

    [Fact]
    public void FitAndFillCropKeepAspect()
    {
        Assert.Contains("--keepaspect=yes", Build(ScalingMode.Fit));
        Assert.Contains("--keepaspect=yes", Build(ScalingMode.FillCrop));
    }
}
