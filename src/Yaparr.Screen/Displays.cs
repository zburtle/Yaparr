using System.Runtime.InteropServices;

namespace Yaparr.Screen;

/// <summary>One monitor, in the coordinates of the whole desktop (which can start at a negative position).</summary>
public sealed record Display(int Index, string Name, int X, int Y, int Width, int Height, bool Primary, long Handle = 0);

/// <summary>Lists the monitors, so that one of them can be shared instead of the whole desktop. Windows, and Linux under X11 (through xrandr).</summary>
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
        if (OperatingSystem.IsLinux()) return ListX11();
        if (!OperatingSystem.IsWindows()) return Array.Empty<Display>();
        var found = new List<Display>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                found.Add(new Display(0, info.Device, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0, monitor.ToInt64()));
            return true;
        }, IntPtr.Zero);
        return Order(found);
    }

    private static IReadOnlyList<Display> Order(IEnumerable<Display> found) =>
        found.OrderByDescending(d => d.Primary).ThenBy(d => d.X).ThenBy(d => d.Y)
            .Select((d, i) => d with { Index = i }).ToList();

    // The X11 monitors come from xrandr. Without it the whole screen is shared as one monitor of unknown size.
    private static IReadOnlyList<Display> ListX11()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("xrandr", "--listmonitors") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                var text = process.StandardOutput.ReadToEndAsync();
                if (process.WaitForExit(5000) && process.ExitCode == 0 && ParseXrandr(text.GetAwaiter().GetResult()) is { Count: > 0 } found) return found;
                if (!process.HasExited) process.Kill();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return new[] { new Display(0, "Screen", 0, 0, 0, 0, true) };
    }

    // " 0: +*DP-1 1920/527x1080/296+0+0  DP-1": the monitor's number, flags with the name, size in pixels/millimetres, position, name.
    private static readonly System.Text.RegularExpressions.Regex XrandrLine = new(
        @"^\s*\d+:\s+(?<flags>\S+)\s+(?<w>\d+)/\d+x(?<h>\d+)/\d+(?<x>[+-]\d+)(?<y>[+-]\d+)\s+(?<name>\S+)",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Reads the output of <c>xrandr --listmonitors</c>; the primary monitor (marked with a star) comes first.</summary>
    public static IReadOnlyList<Display> ParseXrandr(string text)
    {
        var found = new List<Display>();
        foreach (System.Text.RegularExpressions.Match m in XrandrLine.Matches(text))
        {
            var number = System.Globalization.CultureInfo.InvariantCulture;
            found.Add(new Display(0, m.Groups["name"].Value, int.Parse(m.Groups["x"].Value, number), int.Parse(m.Groups["y"].Value, number),
                int.Parse(m.Groups["w"].Value, number), int.Parse(m.Groups["h"].Value, number), m.Groups["flags"].Value.Contains('*')));
        }
        if (found.Count > 0 && !found.Any(d => d.Primary)) found[0] = found[0] with { Primary = true };
        return Order(found);
    }
}
