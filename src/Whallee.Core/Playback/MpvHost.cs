using System.Diagnostics;
using Whallee.Core.Configuration;
using Whallee.Core.Desktop;

namespace Whallee.Core.Playback;

public sealed class MpvHost : IDisposable
{
    /// <summary>
    /// With --wid, mpv renders into a window we already own and never creates one of its own,
    /// so "wait for the child window" is not a usable health check. Staying alive is.
    /// </summary>
    private static readonly TimeSpan LivenessWindow = TimeSpan.FromSeconds(1);

    private readonly Process _process;
    private bool _disposed;

    private MpvHost(Process process, string pipeName)
    {
        _process = process;
        PipeName = pipeName;
    }

    public bool IsRunning => !_process.HasExited;
    public int ProcessId => _process.Id;

    /// <summary>
    /// The name this player's <c>--input-ipc-server</c> pipe was created under (without the
    /// <c>\\.\pipe\</c> prefix). Nothing in Plan 1 connects to it — by design; Plan 2 attaches.
    /// It is stored rather than recomputed so a consumer never has to re-derive it from a loop
    /// index, which cannot be mapped back to a display reliably.
    /// </summary>
    public string PipeName { get; }

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

        // Everything from here to the return must be guarded. mpv is RUNNING now, but it is not
        // yet in the job object and not yet in WallpaperEngine's player list — so if anything
        // below throws unguarded, nothing in the process can ever kill it, JobObjectGuard.Dispose
        // included, and it survives until reboot painting on the user's desktop. The concrete
        // trigger is a truncated or 0-byte clip: mpv exits almost immediately, and if it exits
        // before AssignProcessToJobObject runs, that call fails and the caller gets
        // "AssignProcessToJobObject failed: 6" instead of the informative message below.
        try
        {
            guard.Assign(process);

            // Drain the pipes so a full buffer can never block mpv.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (MessagePump.WaitForExit(process, LivenessWindow))
            {
                var reason = process.ExitCode switch
                {
                    1 => "mpv failed to initialise",
                    2 or 3 => "mpv could not play the file",
                    _ => $"mpv exited with code {process.ExitCode}"
                };
                throw new InvalidOperationException($"{reason} ({videoPath})");
            }
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.Dispose();
            throw;
        }

        return new MpvHost(process, pipeName);
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
        catch (InvalidOperationException) { /* already exited between the check and the kill */ }
        catch (System.ComponentModel.Win32Exception)
        {
            // Kill was refused. Not fatal: the job object owns this process and the OS
            // terminates it when the job handle closes, so it cannot outlive Whallee.
        }
        finally
        {
            _process.Dispose();
        }
    }
}
