using System.Runtime.InteropServices;

namespace Whallee.Core.Displays;

/// <summary>
/// The active displays in the order Windows numbers them in Settings › Display, as GDI device
/// names (\\.\DISPLAY5 and so on).
/// </summary>
/// <remarks>
/// WHY NOT THE NUMBER IN THE DEVICE NAME. That was the first attempt (2026-11-09) and it is wrong
/// on a dock: Windows hands a newly seen dock's screens fresh GDI names, so a laptop plus two
/// docked monitors came out as DISPLAY1, DISPLAY5, DISPLAY6 while Settings calls them 1, 2, 3.
/// QueryDisplayConfig's active paths come back in Settings' order - verified on 2026-11-13 against
/// this machine: DISPLAY1 (laptop), DISPLAY5 (BenQ GW2480), DISPLAY6 (BenQ GW2490).
///
/// Self-contained interop, like ShellWatcher's: these structs are used nowhere else. Any failure
/// returns an empty list, and DisplayLabel falls back to the device-name number.
/// </remarks>
public static class WindowsDisplayOrder
{
    private const uint QdcOnlyActivePaths = 0x2;
    private const uint GetSourceName = 1;

    public static IReadOnlyList<string> Read()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
                return Array.Empty<string>();

            var paths = new Path[pathCount];
            var modes = new Mode[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes,
                                   IntPtr.Zero) != 0)
                return Array.Empty<string>();

            var names = new List<string>();
            for (var i = 0; i < pathCount; i++)
            {
                var request = new SourceName
                {
                    Header = new Header
                    {
                        Type = GetSourceName,
                        Size = (uint)Marshal.SizeOf<SourceName>(),
                        AdapterId = paths[i].Source.AdapterId,
                        Id = paths[i].Source.Id
                    }
                };

                if (DisplayConfigGetDeviceInfo(ref request) == 0 &&
                    !string.IsNullOrEmpty(request.GdiDeviceName) &&
                    !names.Contains(request.GdiDeviceName, StringComparer.OrdinalIgnoreCase))
                    names.Add(request.GdiDeviceName);
            }

            return names;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Source { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Target
    {
        public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology;
        public uint Rotation; public uint Scaling; public uint RefreshNumerator; public uint RefreshDenominator;
        public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Path { public Source Source; public Target Target; public uint Flags; }

    /// <summary>DISPLAYCONFIG_MODE_INFO: 16 bytes of header and a 48-byte union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Mode
    {
        public uint InfoType; public uint Id; public Luid AdapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public uint Type; public uint Size; public Luid AdapterId; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        public Header Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiDeviceName;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint pathCount, [Out] Path[] paths, ref uint modeCount, [Out] Mode[] modes,
        IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
}
