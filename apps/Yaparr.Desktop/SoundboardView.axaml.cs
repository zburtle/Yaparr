using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaparr.Chat;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Desktop;

/// <summary>One tile on the soundboard.</summary>
public sealed class SoundVm : Observable
{
    private bool _playing;

    public SoundVm(SoundInfo info, bool mine)
    {
        Info = info;
        Mine = mine;
        // The same sound always gets the same colour, whoever is looking.
        uint hash = 2166136261;
        foreach (char c in info.Name) hash = (hash ^ c) * 16777619;
        var color = FromHsl(hash % 360, 0.62, 0.58);
        Tint = new SolidColorBrush(color);
        TintBack = new SolidColorBrush(color, 0.2);
    }

    public SoundInfo Info { get; }
    public bool Mine { get; }

    /// <summary>In the room's settings: this person may delete the sound for everyone (they added it, or they are an admin).</summary>
    public bool CanDelete { get; init; }

    /// <summary>In the room's settings: this person may rename the sound (they added it, or they are an admin).</summary>
    public bool CanRename { get; init; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public bool Available => Info.Available;
    public IBrush Tint { get; }
    public IBrush TintBack { get; }

    /// <summary>The first letter (or emoji) of the name, for the badge.</summary>
    public string Initial
    {
        get
        {
            var name = Info.Name.Trim();
            if (name.Length == 0) return "♪";
            var first = StringInfo.GetNextTextElement(name);
            return first.ToUpperInvariant();
        }
    }

    public string Caption => (Info.Available ? "" : "downloading… ") + $"{(Mine ? "You" : Info.OwnerName)} · {Info.Duration.TotalSeconds:0.#} s";

    /// <summary>The number key this sound is on in your own arrangement ("1" to "9", then "0"); empty when it is on none.</summary>
    public string Shortcut { get; init; } = "";
    public bool HasShortcut => Shortcut.Length > 0;

    /// <summary>True for a moment after someone plays this sound.</summary>
    public bool Playing
    {
        get => _playing;
        set { if (_playing == value) return; _playing = value; Raise(); }
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - c / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>One of the ten number keys of the board, with the sound you put on it (or none).</summary>
public sealed class SlotVm
{
    public SlotVm(int index, SoundVm? sound) { Index = index; Sound = sound; }

    public int Index { get; }
    public SoundVm? Sound { get; }
    public string Key => SoundSlots.KeyLabel(Index);
    public bool Filled => Sound != null;
    public string Name => Sound?.Name ?? "";
    public string Tip => Sound == null
        ? $"Key {Key}: nothing yet. Drag a sound here."
        : $"Key {Key}: {Sound.Name}. Click or press {Key} to play it, drag another sound here to replace it, right-click for more.";
}

/// <summary>
/// A room's soundboard: tiles to click, sounds to add, and the controls to quieten them. It sits over the main window or in a
/// <see cref="SoundboardWindow"/> of its own, and moves between them; closing it (or the window) ends it with <see cref="Detach"/>.
/// </summary>
public partial class SoundboardView : UserControl
{
    private const int SearchFrom = 8;
    private static readonly TimeSpan PlayingFor = TimeSpan.FromMilliseconds(700), MessageFor = TimeSpan.FromSeconds(6);
    private const string Hint = "Click a sound, or press its number, to play it for everyone in voice. Drag a sound onto a number to put it there. Right-click for more.";

    /// <summary>Which of the ten keys (from 0) a key press is, or -1 for any other key: 1 to 9 and then 0, on the top row or the number pad.</summary>
    public static int SlotForKey(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1,
        Key.D0 => 9,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
        Key.NumPad0 => 9,
        _ => -1,
    };

    private ChatRoom _room = null!;
    private PeerId _me;
    private AppSettings _settings = new();
    private Action _save = () => { };
    private Func<VoiceSession?> _voice = () => null;
    private Func<string?> _output = () => null;
    private Action _togglePlace = () => { };
    private Action _close = () => { };
    private readonly ObservableCollection<SoundVm> _tiles = new();
    private readonly ObservableCollection<SlotVm> _slots = new();
    private string[] _slotIds = new string[SoundSlots.Count];
    private readonly DispatcherTimer _messageTimer = new() { Interval = MessageFor };
    private bool _loading, _attached;

    // What is dragged: a sound id and the key it comes from ("tile" when it comes from the list of sounds).
    private static readonly DataFormat<string> SoundFormat = DataFormat.CreateStringApplicationFormat("yaparr-soundboard-sound");
    private (string Id, int Slot)? _pressed;
    private Point _pressedAt;
    private PointerPressedEventArgs? _pressedEvent;
    private bool _dragging;

    public SoundboardView()
    {
        InitializeComponent();
        // The board itself can hold the keyboard, so the number keys work when nothing inside it does (after a click on a number, or when the tiles are rebuilt).
        Focusable = true;
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); Footer.Text = Hint; };
        Footer.Text = Hint;
        SlotBar.ItemsSource = _slots;
        // A press on a tile or a key is seen before the button under it takes it (a click must still play), and moving on from it starts a drag.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => { if (!_dragging) _pressed = null; }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(TileScroll, true);
    }

    /// <summary>Starts showing a room's sounds. <paramref name="togglePlace"/> moves the board between the window and a window of its own.</summary>
    internal void Attach(ChatRoom room, PeerId me, AppSettings settings, Action save, Func<VoiceSession?> voice, Func<string?> output,
        Action togglePlace, Action close)
    {
        _room = room; _me = me; _settings = settings; _save = save; _voice = voice; _output = output;
        _togglePlace = togglePlace; _close = close;
        RoomText.Text = room.Name;
        Tiles.ItemsSource = _tiles;

        _loading = true;
        MuteAllBox.IsChecked = settings.SoundboardsMuted;
        VolumeBox.Value = settings.SoundboardVolume;
        _loading = false;

        room.SoundsChanged += OnSoundsChanged;
        room.SoundPlayed += OnSoundPlayed;
        _attached = true;
        Reload();
    }

    /// <summary>Stops listening to the room. The board is not used after this.</summary>
    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _room.SoundsChanged -= OnSoundsChanged;
        _room.SoundPlayed -= OnSoundPlayed;
        _messageTimer.Stop();
    }

    /// <summary>True while the board is over the main window (it can be closed there, and popped out); false in a window of its own.</summary>
    public bool Docked
    {
        set
        {
            DockButton.Content = value ? "⧉ Pop out" : "Show in the app";
            ToolTip.SetTip(DockButton, value ? "Move the soundboard to a window of its own" : "Put the soundboard back over the app");
            CloseButton.IsVisible = value;
        }
    }

    /// <summary>
    /// Gives the board the keyboard, so the number keys play at once. The search box does not take it until you click it or start typing a letter
    /// (see <see cref="OnKeyDown"/>), since digits typed there are not shortcuts.
    /// </summary>
    public void FocusFirst() => Focus();

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private void Say(string text)
    {
        Footer.Text = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void OnSoundsChanged() => Dispatcher.UIThread.Post(() => { if (_attached) Reload(); });

    private void OnSoundPlayed(PeerId by, SoundInfo sound) => Dispatcher.UIThread.Post(async () =>
    {
        if (!_attached) return;
        var who = by == _me ? "You" : _room.VoiceParticipants.FirstOrDefault(p => p.Peer.Id == by)?.Peer.DisplayName ?? "Someone";
        Say($"{who} played {sound.Name}");
        // The tile lights up for a moment, so a press is seen to have gone through.
        var tile = _tiles.FirstOrDefault(t => t.Id == sound.Id);
        if (tile == null) return;
        tile.Playing = true;
        await Task.Delay(PlayingFor);
        tile.Playing = false;
    });

    private void Reload()
    {
        var all = _room.Sounds;
        SearchBox.IsVisible = all.Count >= SearchFrom;
        string query = SearchBox.IsVisible ? SearchBox.Text?.Trim() ?? "" : "";
        // Your own arrangement of the ten number keys; the first ten sounds as they are listed until you change it.
        _slotIds = SoundSlots.Resolve(_settings.SoundboardSlots.TryGetValue(_room.RoomId, out var stored) ? stored : null, all.Select(s => s.Id).ToList());
        var byId = all.ToDictionary(s => s.Id);
        _slots.Clear();
        for (int i = 0; i < SoundSlots.Count; i++)
            _slots.Add(new SlotVm(i, _slotIds[i].Length > 0 && byId.TryGetValue(_slotIds[i], out var onKey) ? new SoundVm(onKey, onKey.Owner == _me) : null));

        // Rebuilding the tiles destroys the one that had the keyboard, and the number keys would stop: the board takes it back.
        bool hadKeyboard = IsKeyboardFocusWithin;
        _tiles.Clear();
        foreach (var s in all)
            if (query.Length == 0 || s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || s.OwnerName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                _tiles.Add(new SoundVm(s, s.Owner == _me) { Shortcut = SoundSlots.KeyLabel(Array.IndexOf(_slotIds, s.Id)) });

        if (hadKeyboard) Dispatcher.UIThread.Post(() => { if (_attached && !IsKeyboardFocusWithin) Focus(); });

        bool none = _tiles.Count == 0;
        EmptyPanel.IsVisible = none;
        if (all.Count == 0)
        {
            EmptyGlyph.Text = "♪";
            EmptyTitle.Text = "No sounds yet";
            EmptyText.Text = "Sounds are added in the room's settings (the ⋯ menu, then Room settings). Everyone in the room can play them while they are in voice.";
        }
        else
        {
            EmptyGlyph.Text = "?";
            EmptyTitle.Text = "Nothing matches";
            EmptyText.Text = $"No sound or person here is called \"{query}\".";
        }
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_attached) Reload();
    }

    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SoundVm tile }) PlaySound(tile.Id);
    }

    // The number keys play what you put on them, while the cursor is not in the search box (where digits are typed).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !_attached) return;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        // A letter starts a search: the cursor goes to the search box (when there is one) and the letter is typed there.
        if (e.Key is >= Key.A and <= Key.Z && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift && SearchBox.IsVisible)
        {
            SearchBox.Focus();
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        int slot = SlotForKey(e.Key);
        if (slot < 0) return;
        e.Handled = true;
        if (_slotIds[slot].Length > 0) PlaySound(_slotIds[slot]);
        else Say($"Nothing is on {SoundSlots.KeyLabel(slot)} yet: drag a sound onto it.");
    }

    private void PlaySound(string id)
    {
        var voice = _voice();
        if (voice == null) Say("Join voice to play sounds.");
        else if (voice.Deafened) Say("You can't play sounds while deafened.");
        else if (!_room.PlaySound(id)) Say("That sound hasn't finished downloading yet.");
    }

    // ---- the number keys: drag a sound onto one, or a key onto another (they swap) -------------------------------------------------------

    private void Slot_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SlotVm { Sound: { } sound } }) PlaySound(sound.Id);
    }

    private void SetSlots(string[] slots)
    {
        _settings.SoundboardSlots[_room.RoomId] = slots.ToList();
        _save();
        Reload();
    }

    private void Slot_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: SlotVm slot } control) return;
        var items = new List<Control>();
        if (slot.Filled)
        {
            var clear = new MenuItem { Header = $"Take it off {slot.Key}" };
            clear.Click += (_, _) => SetSlots(SoundSlots.Clear(_slotIds, slot.Index));
            items.Add(clear);
        }
        var reset = new MenuItem { Header = "Use the first ten sounds again" };
        reset.Click += (_, _) => { _settings.SoundboardSlots.Remove(_room.RoomId); _save(); Reload(); };
        items.Add(reset);
        new ContextMenu { ItemsSource = items }.Open(control);
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = false;
        _pressed = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // A tile or a key: both carry their data in their context.
        if ((e.Source as StyledElement)?.DataContext is SoundVm tile) _pressed = (tile.Id, -1);
        else if ((e.Source as StyledElement)?.DataContext is SlotVm { Sound: { } onKey } slot) _pressed = (onKey.Id, slot.Index);
        _pressedAt = e.GetPosition(this);
        _pressedEvent = e;
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || _pressedEvent is not { } trigger || _dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _pressedAt.X) < 6 && Math.Abs(at.Y - _pressedAt.Y) < 6) return;
        _dragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(SoundFormat, $"{pressed.Slot}|{pressed.Id}"));
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _pressed = null;
        }
    }

    // What is being dragged: the key it comes from (-1 for the list of sounds) and the sound.
    private static (int From, string Id)? Dragged(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(SoundFormat) is not { } text) return null;
        int bar = text.IndexOf('|');
        return bar > 0 && int.TryParse(text.AsSpan(0, bar), out int from) ? (from, text[(bar + 1)..]) : null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool onKey = (e.Source as StyledElement)?.DataContext is SlotVm;
        bool onList = !onKey && IsInTiles(e.Source);
        e.DragEffects = Dragged(e) is { } d && (onKey || (onList && d.From >= 0)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Dragged(e) is not { } d) return;
        if ((e.Source as StyledElement)?.DataContext is SlotVm target) SetSlots(SoundSlots.Put(_slotIds, d.Id, target.Index));
        else if (IsInTiles(e.Source) && d.From >= 0) SetSlots(SoundSlots.Clear(_slotIds, d.From));   // a key dropped back on the list leaves its sound off the keys
        e.Handled = true;
    }

    private bool IsInTiles(object? source)
    {
        for (var visual = source as Visual; visual != null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, TileScroll)) return true;
        return false;
    }

    private void Tile_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: SoundVm tile } button) return;
        var items = new List<Control>();
        var preview = new MenuItem { Header = "Preview (only you hear it)", IsEnabled = tile.Available };
        preview.Click += (_, _) => Preview(tile);
        items.Add(preview);
        if (tile.Mine)
        {
            var rename = new MenuItem { Header = "Rename…" };
            rename.Click += async (_, _) => await RenameAsync(tile);
            var remove = new MenuItem { Header = "Delete" };
            remove.Click += async (_, _) => await RemoveAsync(tile);
            items.Add(new Separator());
            items.Add(rename);
            items.Add(remove);
        }
        else
        {
            if (_room.MyRole >= RoomRole.Admin)
            {
                items.Add(new Separator());
                var renameAny = new MenuItem { Header = "Rename… (admin)" };
                renameAny.Click += async (_, _) => await RenameAsync(tile);
                items.Add(renameAny);
                var delete = new MenuItem { Header = "Delete (admin)" };
                delete.Click += async (_, _) => await AdminDeleteAsync(tile);
                items.Add(delete);
            }
        }
        new ContextMenu { ItemsSource = items }.Open(button);
        e.Handled = true;
    }

    private void Preview(SoundVm tile)
    {
        if (_room.GetSound(tile.Id) is not { } clip) return;
        try { VoiceAudio.PlayOnce(clip.ToSamples(), _output()); }
        catch (Exception ex) { Say("Couldn't play the sound: " + ex.Message); }
    }

    private async Task RenameAsync(SoundVm tile)
    {
        var name = await PromptDialog.AskAsync(Owner, "New name for the sound:", tile.Name);
        if (string.IsNullOrWhiteSpace(name) || name == tile.Name) return;
        try { _room.RenameSound(tile.Id, name); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { Say(ex.Message); }
    }

    private async Task AdminDeleteAsync(SoundVm tile)
    {
        if (Owner is not { } owner || !await Dialogs.ConfirmAsync(owner, "Delete sound", $"Remove \"{tile.Name}\" from the soundboard for everyone? It was added by {tile.Info.OwnerName}.", "Delete", "Keep")) return;
        try { _room.DeleteSound(tile.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { Say(ex.Message); }
    }

    private async Task RemoveAsync(SoundVm tile)
    {
        if (Owner is not { } owner || !await Dialogs.ConfirmAsync(owner, "Delete sound", $"Remove \"{tile.Name}\" from the soundboard for everyone?", "Delete", "Keep")) return;
        try { _room.RemoveSound(tile.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { Say(ex.Message); }
    }

    private void Dock_Click(object? sender, RoutedEventArgs e) => _togglePlace();

    private void Close_Click(object? sender, RoutedEventArgs e) => _close();

    private void MuteAll_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool muted = MuteAllBox.IsChecked == true;
        _settings.SoundboardsMuted = muted;
        if (_voice() is { } voice) voice.SoundboardsMuted = muted;
        _save();
    }

    private void Volume_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.SoundboardVolume = (int)VolumeBox.Value;
        if (_voice() is { } voice) voice.SoundboardVolume = _settings.SoundboardVolume / 100f;
        _save();
    }
}
