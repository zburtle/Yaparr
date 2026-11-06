using Avalonia.Controls;

namespace Yaparr.Desktop;

/// <summary>A window of its own for a shared screen. Closing it stops watching, unless the picture was handed back to the main window.</summary>
public sealed class ScreenViewWindow : Window
{
    public ScreenViewWindow(ScreenViewControl view)
    {
        Title = view.ShareName;
        Width = 960;
        Height = 560;
        MinWidth = 320;
        MinHeight = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = view;
        Closed += (_, _) => { if (!HandedBack) view.StopWatching(); };
    }

    /// <summary>Set before closing when the picture goes back into the main window, so closing does not end the watching.</summary>
    public bool HandedBack { get; set; }
}
