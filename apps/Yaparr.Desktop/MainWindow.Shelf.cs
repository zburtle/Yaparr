using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// The GIF shelf: pictures kept to send again with one click. "My shelf" is a folder on this computer (see GifShelf); nothing is looked up online.
// "This room" is the room's shared shelf, which every member can see and send from (see ChatRoom.Gifs); it is fetched when the tab is opened. Pictures are
// added to it and taken off it in the room's settings (MainWindow.RoomSettings.cs), not here.
public partial class MainWindow
{
    private GifShelf? _shelf;
    private GifShelf Shelf => _shelf ??= new GifShelf(_data.ShelfDirectory);

    private bool _shelfShowsRoom;
    private ChatRoom? _shelfRoom;
    private readonly Dictionary<string, byte[]?> _roomPreviews = new();

    private async void Shelf_Click(object? sender, RoutedEventArgs e)
    {
        if (ShelfPopup.IsOpen) { ShelfPopup.IsOpen = false; return; }
        _shelfShowsRoom = false;
        ShelfPopup.IsOpen = true;
        await ShowShelfTabAsync();
    }

    private void ShelfPopup_Closed(object? sender, EventArgs e)
    {
        if (_shelfRoom != null) _shelfRoom.GifsChanged -= OnRoomGifsChanged;
        _shelfRoom = null;
        _shelfVersion++;
    }

    private async void ShelfMine_Click(object? sender, RoutedEventArgs e) { _shelfShowsRoom = false; await ShowShelfTabAsync(); }

    private async void ShelfRoom_Click(object? sender, RoutedEventArgs e) { _shelfShowsRoom = true; await ShowShelfTabAsync(); }

    private async Task ShowShelfTabAsync()
    {
        var room = SelectedRoom;
        ShelfTabs.IsVisible = room != null;
        if (room == null) _shelfShowsRoom = false;
        ShelfMineTab.Classes.Set("primary", !_shelfShowsRoom);
        ShelfRoomTab.Classes.Set("primary", _shelfShowsRoom);
        if (_shelfRoom != null && _shelfRoom != room) _shelfRoom.GifsChanged -= OnRoomGifsChanged;
        if (_shelfShowsRoom && room != null && _shelfRoom != room)
        {
            _shelfRoom = room;
            room.GifsChanged += OnRoomGifsChanged;
        }
        if (_shelfShowsRoom && room != null) room.RequestGifs();
        await FillShelfAsync();
    }

    private void OnRoomGifsChanged() => Dispatcher.UIThread.Post(async () =>
    {
        if (ShelfPopup.IsOpen && _shelfShowsRoom) await FillShelfAsync();
    });

    private int _shelfVersion;

    private async Task FillShelfAsync()
    {
        int version = ++_shelfVersion;
        var room = SelectedRoom;
        bool roomTab = _shelfShowsRoom && room != null;
        // The room's pictures are only sent from here; they are added and taken down in the room's settings.
        ShelfAddButton.IsVisible = !roomTab;
        int total;
        if (roomTab)
        {
            ShelfEmpty.Text = "Nothing here yet. Pictures are added in the room's settings (the ⋯ menu, then Room settings).";
            await FillRoomTilesAsync(room!, ShelfGrid, ShelfEmpty, ShelfCount, () => version != _shelfVersion, editing: false);
            return;
        }

        var items = await Task.Run(() => Shelf.List());
        if (version != _shelfVersion) return;
        total = items.Count;
        ShelfEmpty.Text = "Nothing here yet. Add GIFs and pictures you like to send again, or right-click one in a chat and choose Add to my GIF shelf. They stay on this computer.";
        ShelfGrid.Children.Clear();
        ShelfEmpty.IsVisible = total == 0;
        ShelfCount.Text = Count(total);
        // Previews are made and loaded a few at a time so a long shelf opens at once and fills in.
        foreach (var chunk in items.Chunk(24))
        {
            var loaded = await Task.Run(() => chunk.Select(item =>
            {
                var preview = Shelf.Preview(item.Id);
                return (item, preview, bitmap: preview == null ? null : new Bitmap(new MemoryStream(preview)));
            }).ToList());
            if (version != _shelfVersion) { foreach (var (_, _, bitmap) in loaded) bitmap?.Dispose(); return; }
            foreach (var (item, preview, bitmap) in loaded) ShelfGrid.Children.Add(MyTile(item, preview, bitmap));
        }
    }

    // Fills a grid with the room's pictures. In the picker a click sends one; in the room's settings (editing) a click opens its options (add it to
    // my shelf, remove it, take it down).
    private async Task FillRoomTilesAsync(ChatRoom room, Panel grid, TextBlock empty, TextBlock count, Func<bool> stale, bool editing)
    {
        var gifs = await Task.Run(() => room.Gifs);
        if (stale()) return;
        grid.Children.Clear();
        empty.IsVisible = gifs.Count == 0;
        count.Text = Count(gifs.Count);
        foreach (var chunk in gifs.Chunk(24))
        {
            var loaded = await Task.Run(() => chunk.Select(gif =>
            {
                byte[]? preview = null;
                if (gif.Available)
                {
                    lock (_roomPreviews)
                        if (!_roomPreviews.TryGetValue(gif.Hash, out preview))
                        {
                            preview = room.GetGif(gif.Id) is { } file ? GifShelf.PreviewOf(file) : null;
                            _roomPreviews[gif.Hash] = preview;
                        }
                }
                return (gif, preview, bitmap: preview == null ? null : new Bitmap(new MemoryStream(preview)));
            }).ToList());
            if (stale()) { foreach (var (_, _, bitmap) in loaded) bitmap?.Dispose(); return; }
            foreach (var (gif, preview, bitmap) in loaded) grid.Children.Add(RoomTile(room, gif, preview, bitmap, editing));
        }
    }

    private static string Count(int n) => n == 0 ? "" : n == 1 ? "1 picture" : n + " pictures";

    // A tile for a picture. A GIF plays while its tile is on screen: the file is read when the tile scrolls into view and let go when it scrolls
    // out, so a long shelf keeps only what is visible in memory.
    private Button Tile(bool isGif, byte[]? preview, Bitmap? bitmap, string id, string tip, Func<byte[]?> read, Func<Task>? send, bool menuOnly, params (string Header, Func<Task> Action)[] menu)
    {
        PictureView? player = null;
        Control content;
        if (isGif && bitmap != null)
        {
            player = new PictureView { Fit = true, MaxFrameSide = 240, Thumbnail = preview, PixelWidth = bitmap.PixelSize.Width, PixelHeight = bitmap.PixelSize.Height, IsHitTestVisible = false };
            content = player;
        }
        else content = bitmap != null
            ? new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.Uniform }
            : new TextBlock { Text = "…", Opacity = 0.5, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var button = new Button
        {
            Width = 104, Height = 104, Padding = new Thickness(0), Margin = new Thickness(3), Background = Avalonia.Media.Brushes.Transparent,
            ClipToBounds = true, CornerRadius = new CornerRadius(8), Tag = id,
            Content = content, IsEnabled = send != null || menuOnly,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            ContextMenu = new ContextMenu(),
        };
        if (player != null)
        {
            bool playing = false;
            button.EffectiveViewportChanged += async (_, e) =>
            {
                bool inView = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
                if (inView == playing) return;
                playing = inView;
                if (!inView) { player.Data = null; return; }
                var data = await Task.Run(read);
                if (playing) player.Data = data;
            };
        }
        ToolTip.SetTip(button, tip);
        foreach (var (header, action) in menu)
        {
            var item = new MenuItem { Header = header };
            item.Click += async (_, _) => await action();
            button.ContextMenu.Items.Add(item);
        }
        if (send != null) button.Click += async (_, _) => await send();
        else if (menuOnly) button.Click += (_, _) => button.ContextMenu.Open(button);
        return button;
    }

    private Button MyTile(ShelfItem item, byte[]? preview, Bitmap? bitmap)
    {
        var menu = new List<(string, Func<Task>)>();
        menu.Add(("_Remove from my shelf", async () => { Shelf.Remove(item.Id); await FillShelfAsync(); }));
        return Tile(item.IsGif, preview, bitmap, item.Id,
            $"{(item.IsGif ? "GIF" : "Picture")}, {FormatSize(item.Bytes)}. Click to send it, right-click for more.",
            () => Shelf.Read(item.Id), () => SendFromShelfAsync(item), false, menu.ToArray());
    }

    private Button RoomTile(ChatRoom room, GifInfo gif, byte[]? preview, Bitmap? bitmap, bool editing)
    {
        var menu = new List<(string, Func<Task>)>();
        if (gif.Available)
            menu.Add(("Add to my _shelf", async () =>
            {
                try { if (room.GetGif(gif.Id) is { } data) { await Task.Run(() => Shelf.Add(data)); StatusText.Text = "Added to your GIF shelf."; } }
                catch (Exception ex) when (ex is ArgumentException or IOException) { StatusText.Text = ex.Message; }
            }));
        bool mine = _node != null && gif.Owner == _node.Id;
        if (editing && mine)
            menu.Add(("_Remove from the room's shelf", async () =>
            {
                try { room.RemoveGif(gif.Id); } catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
                await FillRoomShelfAsync();
            }));
        else if (editing && room.MyRole >= RoomRole.Admin)
            menu.Add(("_Take down for everyone", async () =>
            {
                if (!await Dialogs.ConfirmAsync(this, "Yaparr", $"Take this picture, added by {gif.OwnerName}, off the room's GIF shelf for everyone?", "Take down")) return;
                try { room.DeleteGif(gif.Id); } catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
            }));
        var added = gif.Available ? $"Added by {gif.OwnerName}, {FormatSize(gif.Size)}." : $"Added by {gif.OwnerName}. Still downloading…";
        return Tile(gif.IsGif, preview, bitmap, gif.Id,
            editing ? added + " Click for options." : gif.Available ? added + " Click to send it, right-click for more." : added,
            () => room.GetGif(gif.Id), !editing && gif.Available ? () => SendFromRoomAsync(room, gif) : null, editing, menu.ToArray());
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

    private async Task SendFromRoomAsync(ChatRoom room, GifInfo gif)
    {
        ShelfPopup.IsOpen = false;
        var data = await Task.Run(() => room.GetGif(gif.Id));
        if (data == null) { StatusText.Text = "That picture is no longer on the room's shelf."; return; }
        var extension = gif.Mime switch { "image/gif" => ".gif", "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        await SendPictureAsync(data, gif.Hash[..16] + extension);
    }

    private async void ShelfAdd_Click(object? sender, RoutedEventArgs e)
    {
        await AddPicturesAsync("Add to the GIF shelf", bytes => Shelf.Add(bytes));
        if (ShelfPopup.IsOpen) await FillShelfAsync();
    }

    // Asks for pictures and gives each to <paramref name="add"/> (the person's own shelf, or the room's), then says how it went.
    private async Task AddPicturesAsync(string title, Action<byte[]> add)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
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
                await Task.Run(() => add(bytes));
                added++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { problem = file.Name + ": " + ex.Message; }
        }
        if (problem != null) StatusText.Text = problem;
        else if (added > 0) StatusText.Text = added == 1 ? "Added to the GIF shelf." : $"Added {added} pictures to the GIF shelf.";
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
