using Whallee.Core.Desktop;

namespace Whallee.Core.Tests;

public class DisplayLabelTests
{
    [Theory]
    [InlineData(@"\\.\DISPLAY1", "Display 1")]
    [InlineData(@"\\.\DISPLAY2", "Display 2")]
    [InlineData(@"\\.\display12", "Display 12")]
    public void NumbersADisplayDevice(string deviceName, string expected)
    {
        Assert.Equal(expected, DisplayLabel.For(deviceName));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY")]
    [InlineData(@"\\.\DISPLAY2\Monitor0")]
    [InlineData("Some other name")]
    public void ShowsAnythingElseUnchanged(string deviceName)
    {
        Assert.Equal(deviceName, DisplayLabel.For(deviceName));
    }

    [Fact]
    public void ABlankNameStillReadsAsADisplay()
    {
        Assert.Equal("Display", DisplayLabel.For(null));
    }
}
