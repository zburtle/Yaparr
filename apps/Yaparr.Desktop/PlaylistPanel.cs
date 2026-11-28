using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

/// <summary>
/// The watch playlist of a room (see ChatNode.Playlist): what is to be played next, in order, and who added each. The streamer reorders, removes any item, clears it all and says
/// whether others may add; everyone else removes only what they added, and adds (through the streamer's library) when the streamer allows it and is streaming. It is shown over the
/// picture of a film (a popup of the film's view) and, embedded, under the people of a room that has a playlist.
/// </summary>
public sealed class PlaylistPanel : Border
{
    private readonly ChatRoom _room;
    private readonly string _me;
    private readonly Func<bool> _filmOn;
    private readonly Func<bool, PeerId, Task> _add;
    private readonly Func<Task<bool>> _confirmClear;
    private readonly Action<PlaylistItem> _playNow;
    private readonly Action<string> _say;
    private readonly DispatcherTimer? _timer;

    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8) };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _addButton = new() { Content = "Add…", Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _clearButton = new() { Content = "Clear all", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly CheckBox _allow = new() { Content = "Let others add", FontSize = 12 };
    private bool _setting;

    /// <param name="filmOn">Whether a film is being streamed in the room now (the streamer's computer is what answers requests, so nothing can be added or removed by others without one).</param>
    /// <param name="add">Opens the library to pick from (whether this device is the streamer, and who is).</param>
    /// <param name="embedded">Part of a sidebar rather than a popup: no frame of its own, as wide as it is put.</param>
    /// <param name="playNow">Plays an item now, in place of what is playing (only the streamer has the button).</param>
    public PlaylistPanel(ChatRoom room, string myAccount, Func<bool> filmOn, Func<bool, PeerId, Task> add, Func<Task<bool>> confirmClear, Action<PlaylistItem> playNow, Action<string> say, bool embedded = false)
    {
        _playNow = playNow;
        _room = room;
        _me = myAccount;
        _filmOn = filmOn;
        _add = add;
        _confirmClear = confirmClear;
        _say = say;

        if (embedded)
        {
            Padding = new Thickness(14, 8, 12, 8);
        }
        else
        {
            Width = 400;
            MaxHeight = 460;
            Padding = new Thickness(12);
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("AppBg");
            this[!BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Line");
        }
        _empty.Classes.Add("muted");
        _note.Classes.Add("muted");

        _addButton.Click += async (_, _) =>
        {
            var list = _room.Playlist;
            await _add(_room.HostsPlaylist, list.HostId.Length > 0 ? new PeerId(list.HostId) : default);
        };
        _clearButton.Click += async (_, _) =>
        {
            if (!await _confirmClear()) return;
            Run(() => _room.ClearPlaylist());
        };
        _allow.IsCheckedChanged += (_, _) =>
        {
            if (_setting) return;
            Run(() => _room.SetOthersMayAddToPlaylist(_allow.IsChecked == true));
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 6) };
        Grid.SetColumn(_clearButton, 1);
        header.Children.Add(_title);
        header.Children.Add(_clearButton);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8, 0, 0), Children = { _addButton, _allow } };
        var rows = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new StackPanel { Children = { _list, _empty } } };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(_note, 2);
        Grid.SetRow(footer, 3);
        rows.Children.Add(header);
        rows.Children.Add(scroll);
        rows.Children.Add(_note);
        rows.Children.Add(footer);
        Child = rows;

        _room.PlaylistChanged += OnChanged;
        // Whether a film is on is not an event of the playlist: a panel that stays on screen looks again now and then.
        if (embedded)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Render();
            _timer.Start();
        }
        Render();
    }

    /// <summary>Stops listening to the room (the view of the film this is in has ended, or another room is open).</summary>
    public void Release()
    {
        _room.PlaylistChanged -= OnChanged;
        _timer?.Stop();
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Render);

    // Runs a change; what is refused (nobody to send it to, a full list) shows in the status line.
    private void Run(Action change)
    {
        try { change(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { _say(ex.Message); }
    }

    private void Render()
    {
        var list = _room.Playlist;
        bool host = _room.HostsPlaylist;
        bool streaming = host || _filmOn();
        _title.Text = list.Items.Count == 0 ? "Playlist" : $"Playlist · {list.Items.Count}";
        _list.Children.Clear();
        for (int i = 0; i < list.Items.Count; i++) _list.Children.Add(Row(list.Items[i], i, list, host, streaming));

        _empty.IsVisible = list.Items.Count == 0;
        _empty.Text = host ? "Nothing is queued. Add films and episodes to play after this one." : "Nothing is queued.";
        bool canAdd = host || (streaming && list.OthersMayAdd);
        _addButton.IsVisible = canAdd;
        _clearButton.IsVisible = host;
        _allow.IsVisible = host;
        _setting = true;
        _allow.IsChecked = list.OthersMayAdd;
        _setting = false;
        _clearButton.IsEnabled = list.Items.Count > 0;
        string streamer = list.HostName.Length > 0 ? list.HostName : "The streamer";
        _note.Text = host ? (list.OthersMayAdd ? "Everyone in the room can browse your library and add to the playlist." : "Only you can add. Turn on “Let others add” to let the room pick too.")
            : !streaming ? "Nobody is streaming now. It plays in this order when someone does."
            : canAdd ? "You can add from the streamer's library, and remove what you added."
            : $"{streamer} has not allowed others to add.";
    }

    private Control Row(PlaylistItem item, int index, PlaylistSnapshot list, bool host, bool streaming)
    {
        // An item from another streamer's library cannot be played from this one; it stays until someone removes it.
        bool foreign = list.ServerId.Length > 0 && item.ServerId.Length > 0 && item.ServerId != list.ServerId;
        var number = new TextBlock { Text = (index + 1) + ".", MinWidth = 24, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        var title = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold };
        var who = item.AddedBy == _me ? "you" : item.AddedByName.Length > 0 ? item.AddedByName : "someone";
        var detail = new TextBlock { Text = string.Join(" · ", new[] { item.Subtitle, "added by " + who, foreign ? "from another library" : null }.Where(s => !string.IsNullOrEmpty(s))), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
        detail.Classes.Add("muted");
        var text = new StackPanel { Children = { title, detail }, VerticalAlignment = VerticalAlignment.Center };
        if (foreign) text.Opacity = 0.6;
        ToolTip.SetTip(text, item.Title + (item.Subtitle.Length > 0 ? "\n" + item.Subtitle : "") + "\nadded by " + who);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        Button Small(string content, string tip, Action click, bool enabled = true)
        {
            var b = new Button { Content = content, Classes = { "ghost" }, Padding = new Thickness(6, 2), MinWidth = 24, IsEnabled = enabled };
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, tip + ": " + item.Title);
            b.Click += (_, _) => click();
            return b;
        }
        if (host)
        {
            buttons.Children.Add(Small("▶", "Play now, in place of what is playing", () => _playNow(item), !foreign));
            buttons.Children.Add(Small("▲", "Move up", () => Run(() => _room.MoveInPlaylist(item.Id, up: true)), index > 0));
            buttons.Children.Add(Small("▼", "Move down", () => Run(() => _room.MoveInPlaylist(item.Id, up: false)), index < list.Items.Count - 1));
            buttons.Children.Add(Small("✕", "Remove", () => Run(() => _room.RemoveFromPlaylist(item.Id))));
        }
        else if (item.AddedBy == _me && streaming && list.HostId.Length > 0)
            buttons.Children.Add(Small("✕", "Remove what you added", () => Run(() => _room.AskToRemove(new PeerId(list.HostId), item.Id))));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 2) };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        row.Children.Add(number);
        row.Children.Add(text);
        row.Children.Add(buttons);
        return row;
    }
}
