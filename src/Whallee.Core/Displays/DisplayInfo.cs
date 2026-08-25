namespace Whallee.Core.Displays;

public sealed record DisplayInfo(
    string DeviceId,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary)
{
    public override string ToString() =>
        $"{DeviceName,-16} {Width}x{Height} @ ({X},{Y}){(IsPrimary ? " [primary]" : "")}";
}
