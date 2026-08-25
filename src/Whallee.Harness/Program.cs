using Whallee.Core.Configuration;
using Whallee.Core.Desktop;
using Whallee.Core.Displays;
using Whallee.Core.Playback;

if (args.Length == 0)
{
    Console.WriteLine("usage: Whallee.Harness <displays>");
    return 1;
}

switch (args[0])
{
    case "displays":
        foreach (var display in DisplayEnumerator.Enumerate())
        {
            Console.WriteLine(display);
            Console.WriteLine($"    id: {display.DeviceId}");
        }
        return 0;

    case "workerw":
    {
        var layer = DesktopLayer.Locate();
        if (layer is null)
        {
            Console.WriteLine("FAILED: no WorkerW found. The engine cannot work on this system.");
            return 2;
        }
        Console.WriteLine($"mode:         {layer.Mode}");
        Console.WriteLine($"progman:      0x{layer.Progman:X}");
        Console.WriteLine($"workerW:      0x{layer.WorkerW:X}");
        Console.WriteLine($"shellDefView: 0x{layer.ShellDefView:X}");
        return 0;
    }

    case "one":
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: Whallee.Harness one <video-path>");
            return 1;
        }
        var videoPath = args[1];
        const string mpvPath =
            @"C:\Users\cory.robbins\AppData\Local\Programs\Lively Wallpaper\Plugins\Mpv\mpv.exe";

        var layer = DesktopLayer.Locate();
        if (layer is null)
        {
            Console.WriteLine("FAILED: no WorkerW");
            return 2;
        }
        Console.WriteLine($"desktop layer: {layer.Mode}, workerW 0x{layer.WorkerW:X}");

        var display = DisplayEnumerator.Enumerate().First(d => d.IsPrimary);
        Console.WriteLine($"target: {display}");

        using var guard = new JobObjectGuard();
        using var host = HostWindow.CreateFor(display, layer);
        Console.WriteLine($"host window: 0x{host.Handle:X}");

        using var mpv = MpvHost.Start(
            mpvPath, host.Handle, videoPath, ScalingMode.FillCrop, "whallee-0", guard);
        Console.WriteLine($"mpv pid {mpv.ProcessId} running — press Enter to stop");

        Console.ReadLine();
        return 0;
    }

    default:
        Console.WriteLine($"unknown verb: {args[0]}");
        return 1;
}
