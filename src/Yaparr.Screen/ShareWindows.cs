using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Yaparr.Screen;

/// <summary>A window that can be shared. <paramref name="Handle"/> names it exactly, even when other windows have the same title.</summary>
public sealed record ShareWindow(long Handle, string Title, string Program, int Width, int Height)
{
    public string Label => $"{Title}  -  {Program} ({Width} x {Height})";
}

/// <summary>Lists the windows on the desktop that make sense to share. Windows only.</summary>
public static class ShareWindows
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;

    /// <summary>Whether the window is still open.</summary>
    public static bool Exists(long handle) => OperatingSystem.IsWindows() && IsWindow((IntPtr)handle);

    /// <summary>
    /// The windows you could share: visible, not minimised, with a title, and not hidden helper windows (tool windows, and
    /// windows of other virtual desktops, which Windows reports as cloaked). The biggest first.
    /// </summary>
    public static IReadOnlyList<ShareWindow> List(int? exceptProcessId = null)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ShareWindow>();
        var found = new List<ShareWindow>();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
                if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                int length = GetWindowTextLength(hwnd);
                if (length == 0) return true;
                var text = new StringBuilder(length + 1);
                GetWindowText(hwnd, text, text.Capacity);
                var title = text.ToString().Trim();
                if (title.Length == 0 || !GetWindowRect(hwnd, out var r)) return true;
                int width = r.Right - r.Left, height = r.Bottom - r.Top;
                if (width < 120 || height < 80) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (exceptProcessId == (int)pid) return true;
                string program = "";
                try { using var p = Process.GetProcessById((int)pid); program = p.ProcessName; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
                found.Add(new ShareWindow(hwnd.ToInt64(), title, program, width, height));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            return true;
        }, IntPtr.Zero);
        return found.OrderByDescending(w => (long)w.Width * w.Height).ToList();
    }
}
