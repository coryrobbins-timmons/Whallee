using Whallee.Core.Interop;

namespace Whallee.Core.Displays;

public static class DisplayEnumerator
{
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    public static IReadOnlyList<DisplayInfo> Enumerate()
    {
        var found = new List<DisplayInfo>();

        bool Callback(nint hMonitor, nint hdc, ref RECT rect, nint data)
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info))
                return true; // skip this monitor, keep enumerating

            found.Add(new DisplayInfo(
                DeviceId: ResolveDeviceId(info.szDevice),
                DeviceName: info.szDevice,
                X: info.rcMonitor.Left,
                Y: info.rcMonitor.Top,
                Width: info.rcMonitor.Width,
                Height: info.rcMonitor.Height,
                IsPrimary: (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }

        NativeMethods.EnumDisplayMonitors(0, 0, Callback, 0);
        return found;
    }

    /// <summary>
    /// The stable per-panel identifier, e.g. \\?\DISPLAY#BNQ78F7#5&amp;2f856194&amp;0&amp;UID4612#{guid}.
    /// Falls back to the device name when the driver reports nothing usable.
    /// </summary>
    private static string ResolveDeviceId(string deviceName)
    {
        var device = new DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<DISPLAY_DEVICE>() };
        // EDD_GET_DEVICE_INTERFACE_NAME (0x1) yields the \\?\DISPLAY# form rather than a driver key.
        return NativeMethods.EnumDisplayDevicesW(deviceName, 0, ref device, 0x1) &&
               !string.IsNullOrWhiteSpace(device.DeviceID)
            ? device.DeviceID
            : deviceName;
    }
}
