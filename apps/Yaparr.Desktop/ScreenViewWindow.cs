using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Yaparr.Desktop;

/// <summary>
/// A window of its own for a shared screen. Closing it stops watching, unless the picture was handed back to the main window. It can fill the screen
/// (<see cref="WindowState.FullScreen"/>): on Windows, and on Linux under X11 and Wayland (through XWayland, the desktop's own full screen), in the .deb and the
/// Flatpak alike; F11 goes in and out, Esc comes out.
/// </summary>
public sealed class ScreenViewWindow : Window
{
    private const double SideWidth = 300;

    private readonly ScreenViewControl _view;
    private readonly Grid _layout = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Border _side = new() { Width = SideWidth, IsVisible = false };
    private bool _hasSide;

    public ScreenViewWindow(ScreenViewControl view)
    {
        _view = view;
        Title = view.ShareName;
        Width = 960;
        Height = 560;
        MinWidth = 320;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _side.BorderThickness = new Avalonia.Thickness(1, 0, 0, 0);
        _side[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Line");
        _side[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppBg");
        Grid.SetColumn(_side, 1);
        _layout.Children.Add(view);
        _layout.Children.Add(_side);
        Content = _layout;
        // The space bar pauses the film you play, unless it is being typed somewhere (and it works whatever has the focus, a button that was just clicked included).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || !_view.CanTogglePause || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
            _view.RequestPauseToggle();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closed += (_, _) => { if (!HandedBack) view.StopWatching(); view.IsFullScreen = false; };
    }

    /// <summary>
    /// Puts something to the right of the picture (the room's playlist), or takes it away with null. It is out of the way in full screen. The window grows by its width the
    /// first time, so that the picture keeps the room it had.
    /// </summary>
    public void SetSide(Control? panel)
    {
        _side.Child = panel;
        if (panel != null && !_hasSide && WindowState == WindowState.Normal) Width += SideWidth;
        else if (panel == null && _hasSide && WindowState == WindowState.Normal) Width = Math.Max(MinWidth, Width - SideWidth);
        _hasSide = panel != null;
        _side.IsVisible = _hasSide && WindowState != WindowState.FullScreen;
    }

    /// <summary>The picture goes back into the main window: it is taken out of this one first.</summary>
    public void ReleaseView()
    {
        HandedBack = true;
        _layout.Children.Remove(_view);
        _side.Child = null;
        Content = null;
    }

    /// <summary>Set before closing when the picture goes back into the main window, so closing does not end the watching.</summary>
    public bool HandedBack { get; set; }

    /// <summary>The window went into full screen or came out of it, whoever did it (the button, a key, or the desktop's own shortcut).</summary>
    public event Action<bool>? FullScreenChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty) return;
        bool full = WindowState == WindowState.FullScreen;
        _side.IsVisible = _hasSide && !full;
        if (full == _view.IsFullScreen) return;
        _view.IsFullScreen = full;
        FullScreenChanged?.Invoke(full);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11) { _view.RequestFullScreenToggle(); e.Handled = true; }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) { WindowState = WindowState.Normal; e.Handled = true; }
        base.OnKeyDown(e);
    }
}
