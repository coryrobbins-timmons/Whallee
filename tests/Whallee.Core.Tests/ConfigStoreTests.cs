using Whallee.Core.Configuration;

namespace Whallee.Core.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "whallee-test-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var config = ConfigStore.Load(PathFor("nope.json"));

        Assert.Equal(1, config.Version);
        Assert.Empty(config.Displays);
        Assert.Equal(ScalingMode.FillCrop, config.Scaling);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var path = PathFor("round.json");
        var original = new WhalleeConfig
        {
            LibraryFolder = @"C:\clips",
            MpvPath = @"C:\mpv\mpv.exe",
            Scaling = ScalingMode.Fit,
            StartWithWindows = true,
            Displays =
            {
                new DisplayAssignment
                {
                    DeviceId = @"\\?\DISPLAY#ABC#1",
                    DeviceName = @"\\.\DISPLAY6",
                    Mode = DisplayMode.Pinned,
                    PinnedPath = "nature-1.mp4"
                }
            }
        };

        ConfigStore.Save(original, path);
        var loaded = ConfigStore.Load(path);

        Assert.Equal(@"C:\clips", loaded.LibraryFolder);
        Assert.Equal(@"C:\mpv\mpv.exe", loaded.MpvPath);
        Assert.Equal(ScalingMode.Fit, loaded.Scaling);
        Assert.True(loaded.StartWithWindows);
        var display = Assert.Single(loaded.Displays);
        Assert.Equal(@"\\?\DISPLAY#ABC#1", display.DeviceId);
        Assert.Equal(@"\\.\DISPLAY6", display.DeviceName);
        Assert.Equal(DisplayMode.Pinned, display.Mode);
        Assert.Equal("nature-1.mp4", display.PinnedPath);
    }

    [Fact]
    public void Load_CorruptFile_BacksItUpAndReturnsDefaults()
    {
        var path = PathFor("corrupt.json");
        File.WriteAllText(path, "{ this is not json");

        var config = ConfigStore.Load(path);

        Assert.Empty(config.Displays);
        Assert.True(File.Exists(path + ".bak"), "corrupt config should be preserved as .bak");
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "config.json");

        ConfigStore.Save(new WhalleeConfig(), path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Load_BindsHandEditedKeysRegardlessOfCase()
    {
        // Hand-editing config.json is a documented verification step. Before
        // PropertyNameCaseInsensitive, a key typed in the wrong case bound to nothing at all: the
        // assignment silently stayed on Shuffle and ClipResolver picked at random, which looks
        // exactly like "pinning is broken".
        var path = PathFor("handedited.json");
        File.WriteAllText(path, """
            {
              "libraryfolder": "C:\\clips",
              "displays": [
                { "devicename": "\\\\.\\DISPLAY6", "mode": "Pinned", "pinnedpath": "nature-1.mp4" }
              ]
            }
            """);

        var config = ConfigStore.Load(path);

        Assert.Equal(@"C:\clips", config.LibraryFolder);
        var display = Assert.Single(config.Displays);
        Assert.Equal(@"\\.\DISPLAY6", display.DeviceName);
        Assert.Equal(DisplayMode.Pinned, display.Mode);
        Assert.Equal("nature-1.mp4", display.PinnedPath);
    }

    [Fact]
    public void Save_OverwritesInPlaceAndLeavesNoTempFileBehind()
    {
        // Save writes to a temp file and moves it over the target, so a crash mid-write cannot
        // leave a truncated config. A successful save must leave nothing but the config.
        var path = PathFor("atomic.json");
        ConfigStore.Save(new WhalleeConfig { LibraryFolder = @"C:\first" }, path);

        ConfigStore.Save(new WhalleeConfig { LibraryFolder = @"C:\second" }, path);

        Assert.Equal(@"C:\second", ConfigStore.Load(path).LibraryFolder);
        Assert.False(File.Exists(path + ".tmp"), "the temp file should have been moved, not left");
    }
}
