using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;
using Button = Avalonia.Controls.Button;

namespace Yaparr.AndroidApp;

// Emoji in the message box: the button beside it opens the picker (the one reactions use), where the standard groups are joined by "My emoji" (a folder on this
// phone) and "This room" (shared by everyone in it); a :shortcode that is being typed offers the emoji that match above the box; and a whole :shortcode: becomes
// its emoji as the closing colon is typed. A message that uses a custom emoji carries its picture, so everyone sees it whichever set it came from.
public partial class MainView
{
    private enum EmojiTab { None, Mine, Room }

    private EmojiTab _emojiTab;
    private bool _pickingForComposer;
    private bool _clearingReactSearch;
    private bool _replacingEmoji;
    private int _emojiBarStart = -1;
    private ChatRoom? _emojiRoom;

    // ---- the picker, opened from the message box

    private void EmojiButtonClicked(object? sender, RoutedEventArgs e) => OpenComposerEmoji(EmojiTab.None);

    private void OpenComposerEmoji(EmojiTab tab)
    {
        if (_session == null || _current == null) return;
        _reactingTo = null;
        _pickingForComposer = true;
        if (!_reactWired) WireReactPanel();
        ReactHeading.Text = "Emoji";
        ReactSearch.Text = "";
        ShowReactGroup(0);
        ReactPanel.IsVisible = true;
        if (tab != EmojiTab.None) { _emojiTab = tab; ShowReactCustom(); }
    }

    private void UpdateEmojiTabs()
    {
        // The tabs of yours and the room's are there when reacting too (to react with one); adding and removing is for the message box's picker.
        ReactCustomTabs.IsVisible = _pickingForComposer || _reactingTo != null;
        EmojiRoomTab.IsVisible = _current?.Room != null;
        EmojiMineTab.Classes.Set("primary", _emojiTab == EmojiTab.Mine);
        EmojiRoomTab.Classes.Set("primary", _emojiTab == EmojiTab.Room);
        EmojiAddButton.IsVisible = _emojiTab != EmojiTab.None && _pickingForComposer;
    }

    private void EmojiMineClicked(object? sender, RoutedEventArgs e) { _emojiTab = EmojiTab.Mine; ShowReactCustom(); }

    private void EmojiRoomClicked(object? sender, RoutedEventArgs e) { _emojiTab = EmojiTab.Room; ShowReactCustom(); }

    private void DetachEmojiRoom()
    {
        if (_emojiRoom != null) _emojiRoom.GifsChanged -= OnRoomEmojiChanged;
        _emojiRoom = null;
    }

    private void OnRoomEmojiChanged() => Dispatcher.UIThread.Post(() => { if (ReactPanel.IsVisible && _emojiTab == EmojiTab.Room) ShowReactCustom(); });

    private void ShowReactCustom()
    {
        var room = _current?.Room;
        if (_session == null) return;
        if (_emojiTab == EmojiTab.Room && room == null) _emojiTab = EmojiTab.Mine;
        _clearingReactSearch = true;
        try { ReactSearch.Text = ""; }
        finally { _clearingReactSearch = false; }
        UpdateEmojiTabs();

        DetachEmojiRoom();
        if (_emojiTab == EmojiTab.Room && room != null)
        {
            // The room's emoji are listed from what members announced; their pictures are fetched when they are looked at.
            _emojiRoom = room;
            room.GifsChanged += OnRoomEmojiChanged;
            room.RequestGifs();
        }

        ReactGrid.Children.Clear();
        int count = 0;
        if (_emojiTab == EmojiTab.Mine)
        {
            foreach (var own in _session.OwnEmoji.List())
            {
                if (_session.OwnEmoji.Read(own.Name) is not { } bytes || EmojiPictures.FromBytes(bytes) is not { } image) continue;
                var name = own.Name;
                ReactGrid.Children.Add(EmojiTile(name, image, "Remove from my emoji", () => { _session.OwnEmoji.Remove(name); ShowReactCustom(); }));
                count++;
            }
        }
        else if (room != null)
        {
            foreach (var shared in room.Emoji)
            {
                if (shared.Name == null || room.GetGif(shared.Id) is not { } bytes || EmojiPictures.FromBytes(bytes) is not { } image) continue;
                var id = shared.Id;
                if (_session.IsThisDevice(shared.Owner)) ReactGrid.Children.Add(EmojiTile(shared.Name, image, "Remove from the room's emoji", () => RemoveRoomEmoji(room, id, false)));
                else if (room.MyRole >= RoomRole.Admin) ReactGrid.Children.Add(EmojiTile(shared.Name, image, "Take down for everyone", () => RemoveRoomEmoji(room, id, true)));
                else ReactGrid.Children.Add(EmojiTile(shared.Name, image, null, null));
                count++;
            }
        }
        ReactTitle.Text = count > 0
            ? (_emojiTab == EmojiTab.Mine ? "My emoji" : "This room's emoji") + (_reactingTo != null ? " · tap one to react" : " · type :name: to use one. Hold one to remove it.")
            : _emojiTab == EmojiTab.Mine ? "Nothing here yet. Add a small picture and give it a name." : "No emoji in this room yet. Anyone here can add one.";
    }

    private Button EmojiTile(string name, IImage image, string? removeLabel, Action? remove)
    {
        var tile = new Button
        {
            Content = new Image { Source = image, Width = 36, Height = 36, Stretch = Stretch.Uniform },
            Padding = new Thickness(6),
            Margin = new Thickness(1),
            Background = Brushes.Transparent,
            Tag = ":" + name + ":",
        };
        bool held = false;
        if (removeLabel != null && remove != null && _pickingForComposer)
            tile.Holding += (_, args) =>
            {
                if (args.HoldingState != HoldingState.Started) return;
                held = true;
                args.Handled = true;
                ActionTitle.Text = ":" + name + ":";
                ActionList.Children.Clear();
                AddAction(removeLabel, remove, danger: true);
                ActionPanel.IsVisible = true;
            };
        tile.Click += (sender, args) =>
        {
            if (held) { held = false; return; }
            ReactPicked(sender, args);
        };
        return tile;
    }

    private void RemoveRoomEmoji(ChatRoom room, string id, bool forEveryone)
    {
        try
        {
            if (forEveryone) room.DeleteGif(id);
            else room.RemoveGif(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { ReactTitle.Text = ex.Message; return; }
        ShowReactCustom();
    }

    // ---- adding one

    private static string SuggestedEmojiName(string fileName)
    {
        var chars = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_').ToArray();
        var name = new string(chars).Trim('_');
        if (name.Length > CustomEmojiRules.MaxNameLength) name = name[..CustomEmojiRules.MaxNameLength].TrimEnd('_');
        return name.Length >= CustomEmojiRules.MinNameLength ? name : "emoji";
    }

    private async void EmojiAddClicked(object? sender, RoutedEventArgs e)
    {
        if (_session == null || MainActivity.Instance is not { } activity) return;
        var session = _session;
        var room = _emojiTab == EmojiTab.Room ? _current?.Room : null;
        var picked = await activity.PickPictureAsync();
        if (picked is not { } file) return;
        // A big still picture is shrunk to fit; anything that still does not fit is refused with the reason, below.
        var data = await Task.Run(() => ImageTools.ForEmoji(file.Bytes));
        AskText("What should it be called? You will write it as :name: (2 to 32 letters, digits or underscores).", SuggestedEmojiName(file.Name), name =>
        {
            string? problem = null;
            try
            {
                if (room != null) room.AddEmoji(name, data);
                else session.OwnEmoji.Add(name, data);
            }
            catch (ArgumentException ex) { problem = ex.Message; }
            ShowReactCustom();
            if (problem != null) ReactTitle.Text = problem;
        });
    }

    // ---- using them

    // Names that match what is typed after a colon, with their pictures: your own first, then the room's, each name once.
    private List<(string Name, IImage? Image)> CustomMatches(string typed, int max)
    {
        var found = new List<(string, IImage?)>();
        if (_session == null) return found;
        var seen = new HashSet<string>();
        bool Matches(string name) => name.Contains(typed, StringComparison.OrdinalIgnoreCase);
        foreach (var own in _session.OwnEmoji.List().Where(e => Matches(e.Name)).OrderByDescending(e => e.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
            if (seen.Add(own.Name) && found.Count < max) found.Add((own.Name, _session.OwnEmoji.Read(own.Name) is { } b ? EmojiPictures.FromBytes(b) : null));
        if (_current?.Room is { } room)
            foreach (var shared in room.Emoji.Where(e => e.Name != null && Matches(e.Name)).OrderByDescending(e => e.Name!.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))
                if (seen.Add(shared.Name!) && found.Count < max) found.Add((shared.Name!, room.GetGif(shared.Id) is { } b ? EmojiPictures.FromBytes(b) : null));
        return found;
    }

    // ---- :shortcodes in the message box

    // While a :shortcode and some letters end the text, the emoji that match are offered; a whole :name: that is a standard emoji becomes it.
    private void UpdateEmojiBar()
    {
        if (_replacingEmoji) return;
        var text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        if (EmojiTyping.PendingAt(text, caret) is not { } pending) { HideEmojiBar(); return; }

        if (pending.Closed)
        {
            if (EmojiTyping.FromShortcode(pending.Name) is { } exact) ReplaceInInput(pending.Start, caret, exact.Emoji);
            else HideEmojiBar();
            return;
        }

        // Your own emoji and the room's come first, then the standard ones.
        var choices = CustomMatches(pending.Name, 5).Where(c => c.Image != null).Select(c => new EmojiSuggestion("", ":" + c.Name + ":", c.Image)).ToList();
        choices.AddRange(EmojiTyping.Complete(pending.Name, 8).Select(f => new EmojiSuggestion(f.Entry.Emoji, ":" + f.Code + ":")).Take(Math.Max(3, 8 - choices.Count)));
        if (choices.Count == 0) { HideEmojiBar(); return; }
        _emojiBarStart = pending.Start;
        EmojiList.ItemsSource = choices;
        EmojiBar.IsVisible = true;
    }

    private void HideEmojiBar()
    {
        EmojiBar.IsVisible = false;
        _emojiBarStart = -1;
    }

    private void EmojiSuggestionChosen(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: EmojiSuggestion choice } || _emojiBarStart < 0) return;
        var text = InputBox.Text ?? "";
        ReplaceInInput(_emojiBarStart, Math.Clamp(InputBox.CaretIndex, _emojiBarStart, text.Length), choice.Insert);
        InputBox.Focus();
    }

    // Puts text in place of a part of the message box and the caret after it, without the box taking that for typing.
    private void ReplaceInInput(int start, int end, string with)
    {
        _replacingEmoji = true;
        try
        {
            var text = InputBox.Text ?? "";
            start = Math.Clamp(start, 0, text.Length);
            end = Math.Clamp(end, start, text.Length);
            InputBox.Text = text[..start] + with + text[end..];
            InputBox.CaretIndex = start + with.Length;
        }
        finally { _replacingEmoji = false; }
        HideEmojiBar();
    }

    // What is picked in the picker goes into what is being written, in place of what is selected.
    private void InsertIntoInput(string emoji)
    {
        var text = InputBox.Text ?? "";
        int a = Math.Clamp(Math.Min(InputBox.SelectionStart, InputBox.SelectionEnd), 0, text.Length);
        int b = Math.Clamp(Math.Max(InputBox.SelectionStart, InputBox.SelectionEnd), a, text.Length);
        ReplaceInInput(a, b, emoji);
        InputBox.Focus();
    }
}
