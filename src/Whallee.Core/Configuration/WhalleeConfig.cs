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

    /// <summary>Discovered on first use and persisted, because the winget path is version-stamped.</summary>
    public string? YtDlpPath { get; set; }

    public ScalingMode Scaling { get; set; } = ScalingMode.FillCrop;
    public bool StartWithWindows { get; set; }

    /// <summary>Pause playback while a fullscreen app runs, or the machine is locked.</summary>
    /// <remarks>
    /// DEFAULTS TO FALSE, like its sibling below: the feature is opt-in, so an existing install
    /// behaves exactly as it did until the owner ticks the box. Adding these two fields needs NO
    /// version bump and no migration - a config file written before they existed simply
    /// deserialises them to false.
    /// </remarks>
    public bool PauseOnFullscreen { get; set; }

    /// <summary>Pause playback while running on battery.</summary>
    public bool PauseOnBattery { get; set; }

    public List<DisplayAssignment> Displays { get; set; } = new();
}
