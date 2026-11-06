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

    // Saves the picture of the message under the cursor to a file of your choice, exactly as it was sent (a GIF keeps its animation).
    private async void SavePicture_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageVm { Image: { } data, Attachment: { } attachment }) return;
        var name = SafeFileName(attachment.FileName, attachment.Mime);
        var kind = attachment.Mime switch
        {
            "image/gif" => new Avalonia.Platform.Storage.FilePickerFileType("GIF") { Patterns = new[] { "*.gif" } },
            "image/png" => new Avalonia.Platform.Storage.FilePickerFileType("PNG") { Patterns = new[] { "*.png" } },
            "image/webp" => new Avalonia.Platform.Storage.FilePickerFileType("WebP") { Patterns = new[] { "*.webp" } },
            _ => new Avalonia.Platform.Storage.FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" } },
        };
        try
        {
            var path = await Dialogs.PickSaveAsync(this, "Save picture", name, kind);
            if (path == null) return;
            await File.WriteAllBytesAsync(path, data);
            StatusText.Text = "Saved " + Path.GetFileName(path) + ".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Yaparr", "Couldn't save the picture: " + ex.Message);
        }
    }

    // The name it was sent under, cleaned of anything a file name cannot hold, with the extension its type needs.
    internal static string SafeFileName(string? sent, string mime)
    {
        var extension = mime switch { "image/gif" => ".gif", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        var name = new string((sent ?? "").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimStart('.');
        if (name.Length == 0) name = "picture";
        return name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : Path.ChangeExtension(name, extension);
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
