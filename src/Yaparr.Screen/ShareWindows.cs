using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Yaparr.Screen;

/// <summary>A window that can be shared. <paramref name="Handle"/> names it exactly, even when other windows have the same title.</summary>
public sealed record ShareWindow(long Handle, string Title, string Program, int Width, int Height)
{
    public string Label => $"{Title}  -  {Program} ({Width} x {Height})";
}

/// <summary>Lists the windows on the desktop that make sense to share. Windows, and Linux under X11 (see <see cref="X11Windows"/>).</summary>
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
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    private const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;

    /// <summary>The program that owns the window, or 0 if it is gone.</summary>
    public static int ProcessId(long handle)
    {
        if (OperatingSystem.IsLinux()) return X11Windows.ProcessId(handle);
        if (!OperatingSystem.IsWindows()) return 0;
        GetWindowThreadProcessId((IntPtr)handle, out uint pid);
        return (int)pid;
    }

    /// <summary>
    /// The size of the picture Windows Graphics Capture makes of the window: what is inside it, its client area, without the title bar and the borders
    /// (measured: an 800x500 window gives a 784x460 picture, the client area of 784x461 made even). Null where that cannot be asked, and for a minimised window.
    /// </summary>
    public static (int Width, int Height)? CaptureSize(long handle)
    {
        if (!OperatingSystem.IsWindows() || IsIconic((IntPtr)handle) || !GetClientRect((IntPtr)handle, out var rect)) return null;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        return width >= 16 && height >= 16 ? (width, height) : null;
    }

    /// <summary>Whether the window is still open.</summary>
    /// <summary>On Linux, the window's class names (see <see cref="X11Windows.Classes"/>); empty elsewhere.</summary>
    public static IReadOnlyList<string> Classes(long handle) => OperatingSystem.IsLinux() ? X11Windows.Classes(handle) : Array.Empty<string>();

    public static bool Exists(long handle) => OperatingSystem.IsLinux() ? X11Windows.Exists(handle) : OperatingSystem.IsWindows() && IsWindow((IntPtr)handle);

    /// <summary>
    /// The windows you could share: visible, not minimised, with a title, and not hidden helper windows (tool windows, and
    /// windows of other virtual desktops, which Windows reports as cloaked). The biggest first.
    /// </summary>
    public static IReadOnlyList<ShareWindow> List(int? exceptProcessId = null)
    {
        if (OperatingSystem.IsLinux()) return X11Windows.List(exceptProcessId);
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
