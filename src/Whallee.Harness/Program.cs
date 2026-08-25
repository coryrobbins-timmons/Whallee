using Whallee.Core.Desktop;
using Whallee.Core.Displays;

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

    default:
        Console.WriteLine($"unknown verb: {args[0]}");
        return 1;
}
