using Avalonia.Controls;

namespace Yaparr.Desktop;

/// <summary>A window of its own for the soundboard, for keeping it beside the call (on another monitor, say). It holds the same <see cref="SoundboardView"/> that sits over the main window.</summary>
public sealed class SoundboardWindow : Window
{
    public SoundboardWindow(SoundboardView view, string room)
    {
        Title = $"Soundboard - {room}";
        Width = 640;
        Height = 520;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = view;
    }

    /// <summary>Set before closing when the board goes back over the main window, so closing does not end it.</summary>
    public bool HandedBack { get; set; }
}
