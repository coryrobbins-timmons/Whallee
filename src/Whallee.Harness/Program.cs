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

    default:
        Console.WriteLine($"unknown verb: {args[0]}");
        return 1;
}
