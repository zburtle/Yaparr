using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

// The GIF shelf: pictures kept on this computer to send again with one click. It is only a folder (see GifShelf); nothing is looked up online.
public partial class MainWindow
{
    private GifShelf? _shelf;
    private GifShelf Shelf => _shelf ??= new GifShelf(_data.ShelfDirectory);

    private async void Shelf_Click(object? sender, RoutedEventArgs e)
    {
        if (ShelfPopup.IsOpen) { ShelfPopup.IsOpen = false; return; }
        ShelfPopup.IsOpen = true;
        await FillShelfAsync();
    }

    private int _shelfVersion;

    private async Task FillShelfAsync()
    {
        int version = ++_shelfVersion;
        var items = await Task.Run(() => Shelf.List());
        if (version != _shelfVersion) return;
        ShelfGrid.Children.Clear();
        ShelfEmpty.IsVisible = items.Count == 0;
        ShelfCount.Text = items.Count == 0 ? "" : items.Count == 1 ? "1 picture" : items.Count + " pictures";
        // Previews are made and loaded a few at a time so a long shelf opens at once and fills in.
        foreach (var chunk in items.Chunk(24))
        {
            var loaded = await Task.Run(() => chunk.Select(item =>
            {
                var preview = Shelf.Preview(item.Id);
                return (item, bitmap: preview == null ? null : new Bitmap(new MemoryStream(preview)));
            }).ToList());
            if (version != _shelfVersion) { foreach (var (_, bitmap) in loaded) bitmap?.Dispose(); return; }
            foreach (var (item, bitmap) in loaded) ShelfGrid.Children.Add(ShelfTile(item, bitmap));
        }
    }

    private Control ShelfTile(ShelfItem item, Bitmap? bitmap)
    {
        var button = new Button
        {
            Width = 104, Height = 104, Padding = new Thickness(0), Margin = new Thickness(3), Background = Avalonia.Media.Brushes.Transparent,
            ClipToBounds = true, CornerRadius = new CornerRadius(8), Tag = item.Id,
            Content = new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.UniformToFill },
            ContextMenu = new ContextMenu(),
        };
        ToolTip.SetTip(button, $"{(item.IsGif ? "GIF" : "Picture")}, {FormatSize(item.Bytes)}. Click to send it, right-click to take it off the shelf.");
        var remove = new MenuItem { Header = "_Remove from the shelf" };
        remove.Click += async (_, _) => { Shelf.Remove(item.Id); await FillShelfAsync(); };
        button.ContextMenu.Items.Add(remove);
        button.Click += async (_, _) => await SendFromShelfAsync(item);
        return button;
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private async Task SendFromShelfAsync(ShelfItem item)
    {
        ShelfPopup.IsOpen = false;
        var data = await Task.Run(() => Shelf.Read(item.Id));
        if (data == null) { StatusText.Text = "That picture is no longer on the shelf."; return; }
        Shelf.Used(item.Id);
        await SendPictureAsync(data, item.Id);
    }

    private async void ShelfAdd_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add to the GIF shelf",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Pictures") { Patterns = new[] { "*.gif", "*.png", "*.jpg", "*.jpeg", "*.webp" } } },
        });
        int added = 0;
        string? problem = null;
        foreach (var file in files)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                var bytes = copy.ToArray();
                await Task.Run(() => Shelf.Add(bytes));
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { problem = file.Name + ": " + ex.Message; }
        }
        if (problem != null) StatusText.Text = problem;
        else if (added > 0) StatusText.Text = added == 1 ? "Added to the GIF shelf." : $"Added {added} pictures to the GIF shelf.";
        if (ShelfPopup.IsOpen) await FillShelfAsync();
    }

    // From a picture in the chat, exactly as it was sent.
    private async void AddToShelf_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageVm { Image: { } data }) return;
        try
        {
            await Task.Run(() => Shelf.Add(data));
            StatusText.Text = "Added to the GIF shelf.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { StatusText.Text = ex.Message; }
    }
}
