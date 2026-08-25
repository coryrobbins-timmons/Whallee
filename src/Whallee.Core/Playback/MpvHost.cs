using System.Diagnostics;
using Whallee.Core.Configuration;

namespace Whallee.Core.Playback;

public sealed class MpvHost : IDisposable
{
    /// <summary>
    /// With --wid, mpv renders into a window we already own and never creates one of its own,
    /// so "wait for the child window" is not a usable health check. Staying alive is.
    /// </summary>
    private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private bool _disposed;

    private MpvHost(Process process) => _process = process;

    public bool IsRunning => !_process.HasExited;
    public int ProcessId => _process.Id;

    public static MpvHost Start(
        string mpvPath, nint hostWindow, string videoPath,
        ScalingMode scaling, string pipeName, JobObjectGuard guard)
    {
        if (!File.Exists(mpvPath))
            throw new FileNotFoundException($"mpv not found at {mpvPath}", mpvPath);
        if (!File.Exists(videoPath))
            throw new FileNotFoundException($"video not found at {videoPath}", videoPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = mpvPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in MpvOptions.Build(hostWindow, videoPath, scaling, pipeName))
            startInfo.ArgumentList.Add(arg);   // no manual quoting; paths contain spaces

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null for mpv");

        guard.Assign(process);

        // Drain the pipes so a full buffer can never block mpv.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (process.WaitForExit((int)LivenessWindow.TotalMilliseconds))
        {
            var reason = process.ExitCode switch
            {
                1 => "mpv failed to initialise",
                2 or 3 => "mpv could not play the file",
                _ => $"mpv exited with code {process.ExitCode}"
            };
            throw new InvalidOperationException($"{reason} ({videoPath})");
        }

        return new MpvHost(process);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* already gone */ }
        _process.Dispose();
    }
}
