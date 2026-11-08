using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace Yaparr.Desktop;

public partial class MainWindow
{
    // Windows draws its minimise, maximise and close buttons over the top right of the window; this is the room the top row leaves for them.
    private const double CaptionButtonsWidth = 140;

    private const double TitleBarHeight = 32;

    /// <summary>
    /// On Windows, the menu row doubles as the title bar: the window's own title strip goes, and the row takes its place with the buttons still the
    /// system's (so snapping and dragging work as usual). Elsewhere the desktop draws its own title bar, which stays.
    /// </summary>
    private void UseMenuRowAsTitleBar()
    {
        if (!OperatingSystem.IsWindows()) return;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        TopBar.Height = TitleBarHeight;
        TopBar.Margin = new Thickness(0, 0, CaptionButtonsWidth, 0);
        TopBarIcon.IsVisible = true;
        TopBarTitle.IsVisible = true;
        Opened += (_, _) => HideSystemTitleText();
    }

    // Windows draws the window's title (the name of the app and of the person) at the left of the top row, over the menus. The name of the app goes in the
    // middle of the row instead, so the system is told not to draw its title or icon. The window keeps its title, which the taskbar shows.
    private void HideSystemTitleText()
    {
        if (TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        var options = new ThemeOptions { Flags = NoDrawCaption | NoDrawIcon, Mask = NoDrawCaption | NoDrawIcon };
        try { SetWindowThemeAttribute(handle, NonClientAttribute, ref options, (uint)Marshal.SizeOf<ThemeOptions>()); }
        catch (Exception ex) { _log.Write("title bar: could not hide the system title: " + ex.Message); }
    }

    private const int NonClientAttribute = 1;
    private const uint NoDrawCaption = 0x1;
    private const uint NoDrawIcon = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThemeOptions
    {
        public uint Flags;
        public uint Mask;
    }

    [DllImport("uxtheme.dll")]
    private static extern int SetWindowThemeAttribute(IntPtr window, int attribute, ref ThemeOptions options, uint size);
}
