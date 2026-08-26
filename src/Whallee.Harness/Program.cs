using Whallee.Core;
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

        // Read the line on a background thread so this one — which owns the host window,
        // and whose input queue Explorer's desktop is now attached to — keeps pumping.
        MessagePump.RunUntil(Task.Run(Console.ReadLine));
        return 0;
    }

    case "all":
    {
        var configPath = ConfigStore.DefaultPath;
        var config = ConfigStore.Load(configPath);

        // First run: seed sensible values for this machine.
        if (string.IsNullOrEmpty(config.LibraryFolder))
        {
            config.LibraryFolder =
                @"C:\Users\cory.robbins\OneDrive - Timmons Group Inc\Documents\coryrob\Whallee\Library";
            config.MpvPath =
                @"C:\Users\cory.robbins\AppData\Local\Programs\Lively Wallpaper\Plugins\Mpv\mpv.exe";
        }

        using var engine = new WallpaperEngine(config);
        engine.Start();

        foreach (var error in engine.LastErrors)
            Console.WriteLine($"error: {error}");

        ConfigStore.Save(config, configPath);
        Console.WriteLine($"config written to {configPath}");
        Console.WriteLine("running — press Enter to stop");

        // AMENDED 2026-08-25: must pump, not Console.ReadLine(). This thread owns the host
        // windows, and SetParent attached its input queue to Explorer's desktop thread — a
        // thread that stops pumping freezes the user's desktop. See Task 13 Step 3.
        MessagePump.RunUntil(Task.Run(Console.ReadLine));
        return engine.LastErrors.Count == 0 ? 0 : 3;
    }

    default:
        Console.WriteLine($"unknown verb: {args[0]}");
        return 1;
}
