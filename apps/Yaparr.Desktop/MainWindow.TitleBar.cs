using Avalonia;
using Avalonia.Controls;

namespace Yaparr.Desktop;

public partial class MainWindow
{
    private const double TitleBarHeight = 32;

    // Windows draws its full screen, minimise, maximise and close buttons over the top right of the window; this is the room they take.
    private const double CaptionButtonsWidth = 190;

    /// <summary>True where the menu row is the title bar (Windows), and the app's name is drawn in the middle of it instead of as the window's title.</summary>
    private bool MenuRowIsTitleBar { get; set; }

    /// <summary>
    /// On Windows, the menu row doubles as the title bar: the window's own title strip goes, and the row takes its place with the buttons still the
    /// system's (so snapping and dragging work as usual). Windows draws the window's title at the left of that row, over the menus, so the window is given
    /// no title and the name of the app is shown in the middle of the row. Elsewhere the desktop draws its own title bar, which stays.
    /// </summary>
    private void UseMenuRowAsTitleBar()
    {
        if (!OperatingSystem.IsWindows()) return;
        MenuRowIsTitleBar = true;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        TopBar.Height = TitleBarHeight;
        TopBarIcon.IsVisible = true;
        TopBarTitle.IsVisible = true;
        Title = "";
        SyncIcon.Margin = new Thickness(0, 0, CaptionButtonsWidth + 4, 0);
    }
}
