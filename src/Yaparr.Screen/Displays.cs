using System.Runtime.InteropServices;

namespace Yaparr.Screen;

/// <summary>One monitor, in the coordinates of the whole desktop (which can start at a negative position).</summary>
public sealed record Display(int Index, string Name, int X, int Y, int Width, int Height, bool Primary);

/// <summary>Lists the monitors, so that one of them can be shared instead of the whole desktop. Windows only.</summary>
public static class Displays
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>The monitors, the primary first and then from left to right.</summary>
    public static IReadOnlyList<Display> List()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<Display>();
        var found = new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                found.Add(new Display(0, info.Device, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return found.OrderByDescending(d => d.Primary).ThenBy(d => d.X).ThenBy(d => d.Y)
            .Select((d, i) => d with { Index = i }).ToList();
    }
}
