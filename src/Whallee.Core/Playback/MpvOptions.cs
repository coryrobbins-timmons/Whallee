using Whallee.Core.Configuration;

namespace Whallee.Core.Playback;

public static class MpvOptions
{
    public static IReadOnlyList<string> Build(
        nint hostWindow, string videoPath, ScalingMode scaling, string ipcPipeName)
    {
        var args = new List<string>
        {
            $"--wid={(long)hostWindow}",
            "--loop-file=inf",
            "--keep-open=yes",
            "--volume=0",              // audio is never enabled, by design
            "--no-audio",
            "--no-osc",                // no on-screen controller
            "--no-osd-bar",
            "--no-input-default-bindings",
            "--input-vo-keyboard=no",
            "--no-window-dragging",
            "--cursor-autohide=no",
            "--stop-screensaver=no",
            "--media-controls=no",     // do not hijack the keyboard's play/pause key
            "--hwdec=auto-safe",       // safer than plain 'auto'
            "--no-config",             // ignore any user mpv.conf on this machine
            "--msg-level=all=info",
            $"--input-ipc-server=\\\\.\\pipe\\{ipcPipeName}"
        };

        args.AddRange(scaling switch
        {
            ScalingMode.Fill     => new[] { "--keepaspect=no" },
            ScalingMode.Fit      => new[] { "--keepaspect=yes", "--panscan=0.0" },
            ScalingMode.FillCrop => new[] { "--keepaspect=yes", "--panscan=1.0" },
            _ => throw new ArgumentOutOfRangeException(nameof(scaling))
        });

        args.Add(videoPath); // must remain last
        return args;
    }
}
