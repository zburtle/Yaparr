using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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

/// <summary>
/// A room's soundboard: tiles to click, sounds to add, and the controls to quieten them. It sits over the main window or in a
/// <see cref="SoundboardWindow"/> of its own, and moves between them; closing it (or the window) ends it with <see cref="Detach"/>.
/// </summary>
public partial class SoundboardView : UserControl
{
    private const int SearchFrom = 8;
    private static readonly TimeSpan PlayingFor = TimeSpan.FromMilliseconds(700), MessageFor = TimeSpan.FromSeconds(6);
    private const string Hint = "Click a sound to play it for everyone in voice. Right-click for more.";

    private ChatRoom _room = null!;
    private PeerId _me;
    private AppSettings _settings = new();
    private Action _save = () => { };
    private Func<VoiceSession?> _voice = () => null;
    private Func<string?> _output = () => null;
    private Action _togglePlace = () => { };
    private Action _close = () => { };
    private readonly ObservableCollection<SoundVm> _tiles = new();
    private readonly DispatcherTimer _messageTimer = new() { Interval = MessageFor };
    private bool _loading, _attached;

    public SoundboardView()
    {
        InitializeComponent();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); Footer.Text = Hint; };
        Footer.Text = Hint;
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

    /// <summary>Puts the cursor where you can start typing a search, or on the first sound.</summary>
    public void FocusFirst()
    {
        if (SearchBox.IsVisible) SearchBox.Focus();
        else (Tiles.ItemsPanelRoot?.Children.FirstOrDefault(c => c.IsEnabled) ?? DockButton).Focus();
    }

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
        _tiles.Clear();
        foreach (var s in all)
            if (query.Length == 0 || s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || s.OwnerName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                _tiles.Add(new SoundVm(s, s.Owner == _me));

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
        if (sender is not Button { DataContext: SoundVm tile }) return;
        var voice = _voice();
        if (voice == null) Say("Join voice to play sounds.");
        else if (voice.Deafened) Say("You can't play sounds while deafened.");
        else if (!_room.PlaySound(tile.Id)) Say("That sound hasn't finished downloading yet.");
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
