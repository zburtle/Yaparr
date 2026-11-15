using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// Custom emoji: small pictures written as :name: in a message. Like the GIF shelf there are two sets: your own (a folder on this computer, "My emoji") and
// the room's (shared by everyone in it, "This room"). They are in the emoji button's picker, in the tabs beside the standard groups, and they are offered
// when a :shortcode is typed. A message that uses one carries its picture, so everyone sees it whichever set it came from (see CustomEmoji).
public partial class MainWindow
{
    private enum CustomTab { None, Mine, Room }

    private CustomTab _customTab;
    private EmojiShelf? _emojiShelf;
    private ChatRoom? _customRoom;
    private bool _clearingEmojiSearch;

    private EmojiShelf OwnEmojiShelf => _emojiShelf ??= new EmojiShelf(_data.EmojiDirectory);

    // ---- using them

    // The picture of an emoji by name: your own first, then the room's (when it has arrived).
    private byte[]? CustomBytes(string name, ChatRoom? room)
    {
        if (OwnEmojiShelf.Read(name) is { } own) return own;
        return room?.Emoji.FirstOrDefault(e => e.Name == name) is { Available: true } info ? room.GetGif(info.Id) : null;
    }

    // The custom emoji a message's words use, ready to travel with it. A name that is neither yours nor the room's is only words.
    private List<CustomEmoji> CustomEmojiFor(string text, ChatRoom? room)
    {
        var used = new List<CustomEmoji>();
        if (_node == null) return used;
        foreach (var name in CustomEmojiRules.NamesIn(text).Take(CustomEmojiRules.MaxPerMessage))
        {
            if (CustomBytes(name, room) is not { } bytes) continue;
            try { used.Add(_node.PrepareEmoji(name, bytes)); }
            catch (ArgumentException) { }
        }
        return used;
    }

    // Names that match what is typed after a colon, with their pictures: your own first, then the room's, each name once.
    private List<(string Name, IImage? Image)> CustomMatches(string typed, int max)
    {
        var found = new List<(string, IImage?)>();
        var seen = new HashSet<string>();
        bool Matches(string name) => name.Contains(typed, StringComparison.OrdinalIgnoreCase);
        foreach (var own in OwnEmojiShelf.List().Where(e => Matches(e.Name)).OrderByDescending(e => e.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
            if (seen.Add(own.Name) && found.Count < max) found.Add((own.Name, OwnEmojiShelf.Read(own.Name) is { } b ? EmojiPictures.FromBytes(b) : null));
        if (SelectedRoom is { } room)
            foreach (var shared in room.Emoji.Where(e => e.Name != null && Matches(e.Name)).OrderByDescending(e => e.Name!.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
                if (seen.Add(shared.Name!) && found.Count < max) found.Add((shared.Name!, room.GetGif(shared.Id) is { } b ? EmojiPictures.FromBytes(b) : null));
        return found;
    }

    // ---- the tabs in the picker (shown when it was opened from the message box)

    private void OpenComposerPicker(CustomTab tab)
    {
        _reactingTo = null;
        _pickingForComposer = true;
        if (!_emojiWired) WireEmojiPicker();
        EmojiSearch.Text = "";
        ShowEmojiGroup(0);
        ReactionPopup.Placement = Avalonia.Controls.PlacementMode.TopEdgeAlignedLeft;
        ReactionPopup.PlacementTarget = ComposerEmojiButton;
        ReactionPopup.IsOpen = true;
        if (tab != CustomTab.None) { _customTab = tab; ShowCustom(); }
        EmojiSearch.Focus();
    }

    private void UpdateCustomTabs()
    {
        CustomTabs.IsVisible = _pickingForComposer;
        RoomEmojiTab.IsVisible = SelectedRoom != null;
        MyEmojiTab.Classes.Set("primary", _customTab == CustomTab.Mine);
        RoomEmojiTab.Classes.Set("primary", _customTab == CustomTab.Room);
        AddEmojiButton.IsVisible = _customTab != CustomTab.None;
    }

    private void MyEmoji_Click(object? sender, RoutedEventArgs e) { _customTab = CustomTab.Mine; ShowCustom(); }

    private void RoomEmoji_Click(object? sender, RoutedEventArgs e) { _customTab = CustomTab.Room; ShowCustom(); }

    private void DetachCustomRoom()
    {
        if (_customRoom != null) _customRoom.GifsChanged -= OnRoomEmojiChanged;
        _customRoom = null;
    }

    private void OnRoomEmojiChanged() => Dispatcher.UIThread.Post(() => { if (ReactionPopup.IsOpen && _customTab == CustomTab.Room) ShowCustom(); });

    private void ShowCustom()
    {
        var room = SelectedRoom;
        if (_customTab == CustomTab.Room && room == null) _customTab = CustomTab.Mine;
        _clearingEmojiSearch = true;
        try { EmojiSearch.Text = ""; }
        finally { _clearingEmojiSearch = false; }
        UpdateCustomTabs();

        DetachCustomRoom();
        if (_customTab == CustomTab.Room && room != null)
        {
            // The room's emoji are listed from what members announced; their pictures are fetched when they are looked at.
            _customRoom = room;
            room.GifsChanged += OnRoomEmojiChanged;
            room.RequestGifs();
        }

        EmojiGrid.Children.Clear();
        int count = 0;
        if (_customTab == CustomTab.Mine)
        {
            foreach (var own in OwnEmojiShelf.List())
            {
                if (OwnEmojiShelf.Read(own.Name) is not { } bytes || EmojiPictures.FromBytes(bytes) is not { } image) continue;
                EmojiGrid.Children.Add(CustomButton(own.Name, image, "Remove from my emoji", () => { OwnEmojiShelf.Remove(own.Name); ShowCustom(); }));
                count++;
            }
        }
        else if (room != null)
        {
            foreach (var shared in room.Emoji)
            {
                if (shared.Name == null || room.GetGif(shared.Id) is not { } bytes || EmojiPictures.FromBytes(bytes) is not { } image) continue;
                var mine = _node != null && shared.Owner == _node.Id;
                if (mine) EmojiGrid.Children.Add(CustomButton(shared.Name, image, "Remove from the room's emoji", () => RemoveRoomEmoji(room, shared.Id, false), shared));
                else if (room.MyRole >= RoomRole.Admin) EmojiGrid.Children.Add(CustomButton(shared.Name, image, "Take down for everyone", () => RemoveRoomEmoji(room, shared.Id, true), shared));
                else EmojiGrid.Children.Add(CustomButton(shared.Name, image, null, null, shared));
                count++;
            }
        }
        EmojiTitle.Text = count > 0
            ? (_customTab == CustomTab.Mine ? "My emoji" : "This room's emoji") + " · type :name: to use one"
            : _customTab == CustomTab.Mine ? "Nothing here yet. Add a small picture and give it a name." : "No emoji in this room yet. Anyone here can add one.";
    }

    private Button CustomButton(string name, IImage image, string? removeLabel, Action? remove, GifInfo? sharedBy = null)
    {
        var button = new Button
        {
            Content = new Image { Source = image, Width = 28, Height = 28, Stretch = Stretch.Uniform },
            Padding = new Thickness(4),
            Margin = new Thickness(1),
            Background = Brushes.Transparent,
            Tag = ":" + name + ":",
        };
        ToolTip.SetTip(button, ":" + name + ":" + (sharedBy != null ? "\nadded by " + sharedBy.OwnerName : ""));
        button.Click += EmojiPicked_Click;
        if (removeLabel != null && remove != null)
        {
            var item = new MenuItem { Header = removeLabel };
            item.Click += (_, _) => remove();
            button.ContextMenu = new ContextMenu { Items = { item } };
        }
        return button;
    }

    private void RemoveRoomEmoji(ChatRoom room, string id, bool forEveryone)
    {
        try
        {
            if (forEveryone) room.DeleteGif(id);
            else room.RemoveGif(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
        ShowCustom();
    }

    // ---- adding one

    private static string SuggestedEmojiName(string fileName)
    {
        var chars = fileName.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray();
        var name = new string(chars).Trim('_');
        if (name.Length > CustomEmojiRules.MaxNameLength) name = name[..CustomEmojiRules.MaxNameLength].TrimEnd('_');
        return name.Length >= CustomEmojiRules.MinNameLength ? name : "emoji";
    }

    private async void AddEmoji_Click(object? sender, RoutedEventArgs e)
    {
        var tab = _customTab;
        var room = tab == CustomTab.Room ? SelectedRoom : null;
        ReactionPopup.IsOpen = false;
        const string title = "Add an emoji";
        var path = await Dialogs.PickOpenAsync(this, "Choose a picture for the emoji",
            new FilePickerFileType("Pictures") { Patterns = new[] { "*.png", "*.gif", "*.webp", "*.jpg", "*.jpeg" } });
        if (path == null) { OpenComposerPicker(tab); return; }

        byte[] data;
        try
        {
            if (new FileInfo(path).Length > 20L * 1024 * 1024) { await Dialogs.MessageAsync(this, title, "That picture is too big for an emoji."); OpenComposerPicker(tab); return; }
            data = await Task.Run(() => ImageTools.ForEmoji(File.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, title, "Couldn't read that file: " + ex.Message);
            OpenComposerPicker(tab);
            return;
        }

        var name = await PromptDialog.AskAsync(this, "What should it be called? You will write it as :name: (2 to 32 letters, digits or underscores).", SuggestedEmojiName(Path.GetFileNameWithoutExtension(path)));
        if (name != null)
        {
            try
            {
                if (room != null) room.AddEmoji(name, data);
                else OwnEmojiShelf.Add(name, data);
            }
            catch (ArgumentException ex) { await Dialogs.MessageAsync(this, title, ex.Message); }
        }
        OpenComposerPicker(tab);
    }
}
