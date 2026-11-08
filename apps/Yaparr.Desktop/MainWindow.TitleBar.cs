using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yaparr.Desktop;

public partial class MainWindow
{
    // Windows draws its minimise, maximise and close buttons over the top right of the window; this is the room the top row leaves for them.
    private const double CaptionButtonsWidth = 140;

    private const double TitleBarHeight = 32;

    private bool _menuRowIsTitleBar;

    /// <summary>
    /// On Windows, the menu row doubles as the title bar: the window's own title strip goes, and the row takes its place with the buttons still the
    /// system's (so snapping and dragging work as usual). Elsewhere the desktop draws its own title bar, which stays.
    /// </summary>
    private void UseMenuRowAsTitleBar()
    {
        if (!OperatingSystem.IsWindows()) return;
        _menuRowIsTitleBar = true;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        TopBar.Height = TitleBarHeight;
        TopBar.Margin = new Thickness(0, 0, CaptionButtonsWidth, 0);
        TopBarIcon.IsVisible = true;
        TopBarTitle.IsVisible = true;
        Opened += (_, _) => HideDrawnTitle();
        PropertyChanged += (_, e) => { if (e.Property == TitleProperty) HideDrawnTitle(); };
    }

    // The title bar draws the window's title (the name of the app and of the person) at its left, over the menus. The name of the app goes in the middle
    // of the row instead, so that text is hidden. The window keeps its title, which the taskbar shows.
    private void HideDrawnTitle()
    {
        if (!_menuRowIsTitleBar) return;
        // The title bar is built after the window opens (and may be rebuilt), so look a few times rather than once.
        int tries = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            tries++;
            bool found = HideTitleText();
            if (tries >= 20 || (found && tries >= 4))
            {
                timer.Stop();
                if (!found) _log.Write("title bar: the drawn title was not found among " + string.Join(" | ", this.GetVisualDescendants().Select(TextOf).Where(t => !string.IsNullOrEmpty(t)).Take(12)));
            }
        };
        timer.Start();
    }

    private bool HideTitleText()
    {
        bool found = false;
        foreach (var visual in this.GetVisualDescendants())
            if (visual != TopBarTitle && !IsInsideContent(visual) && TextOf(visual) is { } text && text.StartsWith("Yaparr", StringComparison.Ordinal))
            {
                visual.IsVisible = false;
                found = true;
            }
        return found;
    }

    // The words a control shows, if it is one that shows words.
    private static string? TextOf(Visual visual) => visual switch
    {
        TextBlock block => block.Text,
        Avalonia.Controls.Presenters.TextPresenter presenter => presenter.Text,
        ContentControl { Content: string words } => words,
        _ => null,
    };

    // Anything the window's own content shows, as opposed to what the title bar draws around it.
    private bool IsInsideContent(Visual visual) => TopBar.IsVisualAncestorOf(visual) || (Content is Visual content && content.IsVisualAncestorOf(visual));
}
