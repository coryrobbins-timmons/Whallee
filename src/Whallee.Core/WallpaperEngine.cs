using Whallee.Core.Configuration;
using Whallee.Core.Desktop;
using Whallee.Core.Displays;
using Whallee.Core.Playback;

namespace Whallee.Core;

public sealed class WallpaperEngine : IDisposable
{
    private readonly WhalleeConfig _config;
    private readonly ClipResolver _resolver;
    private readonly JobObjectGuard _guard = new();
    private readonly List<HostWindow> _hosts = new();
    private readonly List<MpvHost> _players = new();
    private readonly List<string> _errors = new();

    public IReadOnlyList<string> LastErrors => _errors;

    public WallpaperEngine(WhalleeConfig config)
    {
        _config = config;
        _resolver = new ClipResolver(ClipResolver.ListLibraryFiles, new Random());
    }

    public void Start()
    {
        StopAll();
        _errors.Clear();

        var layer = DesktopLayer.Locate();
        if (layer is null)
        {
            _errors.Add("Could not find the desktop wallpaper layer (WorkerW).");
            return;
        }

        var index = 0;
        foreach (var display in DisplayEnumerator.Enumerate())
        {
            var assignment = MatchAssignment(display);
            var clip = _resolver.Resolve(assignment, _config.LibraryFolder);
            if (clip is null)
            {
                _errors.Add($"{display.DeviceName}: no playable clip in {_config.LibraryFolder}");
                continue;
            }

            try
            {
                var host = HostWindow.CreateFor(display, layer);
                _hosts.Add(host);
                _players.Add(MpvHost.Start(
                    _config.MpvPath, host.Handle, clip, _config.Scaling, $"whallee-{index}", _guard));
            }
            catch (Exception ex)
            {
                // One display failing must not take the others down.
                _errors.Add($"{display.DeviceName}: {ex.Message}");
            }
            index++;
        }
    }

    /// <summary>
    /// Finds this display's saved assignment by stable id, then by device name. Unknown displays
    /// get a Shuffle default and are added to config; saved displays that are absent stay in
    /// config untouched so the assignment returns when the monitor does.
    /// </summary>
    private DisplayAssignment MatchAssignment(DisplayInfo display)
    {
        var match =
            _config.Displays.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.DeviceId) &&
                string.Equals(d.DeviceId, display.DeviceId, StringComparison.OrdinalIgnoreCase))
            ?? _config.Displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
            return match;

        var created = new DisplayAssignment
        {
            DeviceId = display.DeviceId,
            DeviceName = display.DeviceName,
            Mode = DisplayMode.Shuffle
        };
        _config.Displays.Add(created);
        return created;
    }

    public void StopAll()
    {
        foreach (var player in _players) player.Dispose();
        _players.Clear();
        foreach (var host in _hosts) host.Dispose();
        _hosts.Clear();
    }

    public void Dispose()
    {
        StopAll();
        _guard.Dispose();
    }
}
