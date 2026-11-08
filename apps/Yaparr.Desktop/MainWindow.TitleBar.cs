using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yaparr.Desktop;

public partial class MainWindow
{
    // Windows draws its minimise, maximise and close buttons over the top right of the window; this is the room the top row leaves for them.
    private const double CaptionButtonsWidth = 144;

    private const double TitleBarHeight = 32;

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
        TopBar.Margin = new Thickness(0, 0, CaptionButtonsWidth, 0);
        TopBarIcon.IsVisible = true;
        TopBarTitle.IsVisible = true;
        Title = "";
        Opened += (_, _) => HideFullScreenButton();
    }

    // The title bar's own buttons are drawn above the window, not inside it, and include one for full screen, which the app has no use for. They are built
    // after the window opens, so look for it for a few seconds.
    private void HideFullScreenButton()
    {
        int tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            tries++;
            var root = this.GetVisualParent() ?? this;
            var button = root.GetVisualDescendants().FirstOrDefault(v => WindowDecorationProperties.GetElementRole(v) == WindowDecorationsElementRole.FullScreenButton);
            if (button != null) { button.IsVisible = false; timer.Stop(); }
            else if (tries >= 20) { timer.Stop(); _log.Write("title bar: the full screen button was not found"); }
        };
        timer.Start();
    }
}
