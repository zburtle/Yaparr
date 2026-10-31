using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yaparr.Desktop;

// A picture from the chat, shown large over the window. Taking the window's own space (rather than opening another window)
// keeps it a quick look: a click anywhere or Escape puts it away.
public partial class MainWindow
{
    internal void ShowPicture(byte[] data, int pixelWidth, int pixelHeight)
    {
        PictureHost.Child = new PictureView { Fit = true, Data = data, PixelWidth = pixelWidth, PixelHeight = pixelHeight };
        PictureOverlay.IsVisible = true;
        PictureOverlay.Focus();
    }

    private void ClosePicture()
    {
        PictureOverlay.IsVisible = false;
        PictureHost.Child = null;   // leaving the screen also stops a GIF playing
    }

    private void PictureOverlay_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left) ClosePicture();
    }

    private void PictureOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ClosePicture();
        e.Handled = true;
    }
}
