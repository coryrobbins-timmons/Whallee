using Whallee.Core.Desktop;

namespace Whallee.Core.Tests;

public class DisplayLabelTests
{
    private static readonly string[] NoOrder = Array.Empty<string>();

    /// <summary>The docked laptop this was found on: GDI names 1, 5, 6; Settings says 1, 2, 3.</summary>
    private static readonly string[] DockedOrder = { @"\\.\DISPLAY1", @"\\.\DISPLAY5", @"\\.\DISPLAY6" };

    [Theory]
    [InlineData(@"\\.\DISPLAY1", "Display 1")]
    [InlineData(@"\\.\DISPLAY5", "Display 2")]
    [InlineData(@"\\.\display6", "Display 3")]
    public void NumbersByWindowsOrderNotByDeviceName(string deviceName, string expected)
    {
        Assert.Equal(expected, DisplayLabel.For(deviceName, DockedOrder));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", "Display 1")]
    [InlineData(@"\\.\DISPLAY2", "Display 2")]
    [InlineData(@"\\.\display12", "Display 12")]
    public void FallsBackToTheDeviceNameNumberWhenWindowsOrderIsUnknown(string deviceName, string expected)
    {
        Assert.Equal(expected, DisplayLabel.For(deviceName, NoOrder));
    }

    /// <summary>A display just attached and not yet in the order read a moment ago.</summary>
    [Fact]
    public void ADeviceMissingFromTheOrderFallsBackToo()
    {
        Assert.Equal("Display 7", DisplayLabel.For(@"\\.\DISPLAY7", DockedOrder));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY")]
    [InlineData(@"\\.\DISPLAY2\Monitor0")]
    [InlineData("Some other name")]
    public void ShowsAnythingElseUnchanged(string deviceName)
    {
        Assert.Equal(deviceName, DisplayLabel.For(deviceName, NoOrder));
    }

    /// <summary>
    /// Runs the real Windows call. Whatever screens the machine has, every name it returns must be
    /// a GDI display name, and none twice - an empty list (no session, CI) is also acceptable.
    /// </summary>
    [Fact]
    public void WindowsOrderReturnsDistinctGdiNames()
    {
        var order = Whallee.Core.Displays.WindowsDisplayOrder.Read();

        Assert.All(order, n => Assert.StartsWith(@"\\.\DISPLAY", n, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(order.Count, order.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ABlankNameStillReadsAsADisplay()
    {
        Assert.Equal("Display", DisplayLabel.For(null, NoOrder));
    }
}
