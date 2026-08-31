namespace Whallee.Core.Configuration;

public enum DisplayMode { Pinned, Shuffle }

public enum ScalingMode { Fill, Fit, FillCrop }

public sealed class DisplayAssignment
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public DisplayMode Mode { get; set; } = DisplayMode.Shuffle;
    public string? PinnedPath { get; set; }
}

public sealed class WhalleeConfig
{
    public int Version { get; set; } = 1;
    public string LibraryFolder { get; set; } = "";
    public string MpvPath { get; set; } = "";

    /// <summary>Empty means "discover it" — see FfmpegLocator. Set to override discovery.</summary>
    public string FfmpegPath { get; set; } = "";
    public ScalingMode Scaling { get; set; } = ScalingMode.FillCrop;
    public bool StartWithWindows { get; set; }
    public List<DisplayAssignment> Displays { get; set; } = new();
}
