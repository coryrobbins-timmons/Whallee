using Whallee.Core.Displays;

namespace Whallee.Core.Tests;

public class DisplayTopologyTests
{
    private static DisplayInfo Display(
        string id = "ID1", string name = @"\.\DISPLAY1",
        int x = 0, int y = 0, int w = 1920, int h = 1080, bool primary = false) =>
        new(id, name, x, y, w, h, primary);

    [Fact]
    public void Signature_IsStableForTheSameLayout()
    {
        var a = new[] { Display(), Display("ID2", @"\.\DISPLAY2", 1920) };
        var b = new[] { Display(), Display("ID2", @"\.\DISPLAY2", 1920) };

        Assert.Equal(DisplayTopology.Signature(a), DisplayTopology.Signature(b));
    }

    [Fact]
    public void Signature_IgnoresEnumerationOrder()
    {
        var first = Display();
        var second = Display("ID2", @"\.\DISPLAY2", 1920);

        Assert.Equal(
            DisplayTopology.Signature(new[] { first, second }),
            DisplayTopology.Signature(new[] { second, first }));
    }

    [Fact]
    public void Signature_EmptyIsNotTheSameAsPopulated() =>
        Assert.NotEqual(
            DisplayTopology.Signature(Array.Empty<DisplayInfo>()),
            DisplayTopology.Signature(new[] { Display() }));

    [Fact]
    public void HasChanged_IsFalseWhenNothingMoved()
    {
        var displays = new[] { Display(), Display("ID2", @"\.\DISPLAY2", 1920) };
        var signature = DisplayTopology.Signature(displays);

        // The measured bug: a WM_DISPLAYCHANGE arriving with an identical layout must not restart.
        Assert.False(DisplayTopology.HasChanged(signature, displays));
    }

    [Fact]
    public void HasChanged_DetectsAMonitorBeingRemoved()
    {
        var before = DisplayTopology.Signature(
            new[] { Display(), Display("ID2", @"\.\DISPLAY2", 1920) });

        Assert.True(DisplayTopology.HasChanged(before, new[] { Display() }));
    }

    [Fact]
    public void HasChanged_DetectsAMonitorBeingAdded()
    {
        var before = DisplayTopology.Signature(new[] { Display() });

        Assert.True(DisplayTopology.HasChanged(
            before, new[] { Display(), Display("ID2", @"\.\DISPLAY2", 1920) }));
    }

    [Fact]
    public void HasChanged_DetectsAResolutionChange()
    {
        var before = DisplayTopology.Signature(new[] { Display() });

        Assert.True(DisplayTopology.HasChanged(before, new[] { Display(w: 2560, h: 1440) }));
    }

    [Fact]
    public void HasChanged_DetectsAMonitorBeingMoved()
    {
        // The case Plan 1 Task 13 Step 6 was meant to cover: same panel, same mode, new position.
        var before = DisplayTopology.Signature(new[] { Display(x: 1920) });

        Assert.True(DisplayTopology.HasChanged(before, new[] { Display(x: 3840) }));
    }

    [Fact]
    public void HasChanged_DetectsTheNegativeYOriginThisMachineActuallyHas()
    {
        var before = DisplayTopology.Signature(new[] { Display(x: 3840, y: 0, w: 1920, h: 1200) });

        Assert.True(DisplayTopology.HasChanged(
            before, new[] { Display(x: 3840, y: -124, w: 1920, h: 1200) }));
    }

    [Fact]
    public void HasChanged_DetectsPrimaryMovingToAnotherPanel()
    {
        // No single display's bounds change, but the virtual-screen origin shifts under everything.
        var before = DisplayTopology.Signature(new[]
        {
            Display(primary: true), Display("ID2", @"\.\DISPLAY2", 1920)
        });

        Assert.True(DisplayTopology.HasChanged(before, new[]
        {
            Display(), Display("ID2", @"\.\DISPLAY2", 1920, primary: true)
        }));
    }

    [Fact]
    public void HasChanged_DetectsTwoNearIdenticalPanelsSwapping()
    {
        // This machine has two BenQ panels differing by one character of hardware id. If the
        // signature dropped DeviceId, this swap would read as no change at all.
        var before = DisplayTopology.Signature(new[]
        {
            Display("BNQ78E7-UID4611", @"\.\DISPLAY5", 1920),
            Display("BNQ78F7-UID4612", @"\.\DISPLAY6", 0)
        });

        Assert.True(DisplayTopology.HasChanged(before, new[]
        {
            Display("BNQ78E7-UID4611", @"\.\DISPLAY5", 0),
            Display("BNQ78F7-UID4612", @"\.\DISPLAY6", 1920)
        }));
    }
}
