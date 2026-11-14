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
    private readonly ScreenViewControl _view;

    public ScreenViewWindow(ScreenViewControl view)
    {
        _view = view;
        Title = view.ShareName;
        Width = 960;
        Height = 560;
        MinWidth = 320;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = view;
        Closed += (_, _) => { if (!HandedBack) view.StopWatching(); view.IsFullScreen = false; };
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
