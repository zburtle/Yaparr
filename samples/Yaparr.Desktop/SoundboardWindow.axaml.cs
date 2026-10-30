using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Desktop;

/// <summary>One tile on the soundboard.</summary>
public sealed class SoundVm
{
    public SoundVm(SoundInfo info, bool mine)
    {
        Info = info;
        Mine = mine;
    }

    public SoundInfo Info { get; }
    public bool Mine { get; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public bool Available => Info.Available;
    public string Caption => (Info.Available ? "" : "downloading… ") + $"{Info.OwnerName} · {Info.Duration.TotalSeconds:0.#} s";
}

/// <summary>A room's soundboard: tiles to click, sounds to add, and the controls to quieten them.</summary>
public partial class SoundboardWindow : Window
{
    private readonly ChatRoom _room = null!;
    private readonly PeerId _me;
    private readonly AppSettings _settings = new();
    private readonly Action _save = () => { };
    private readonly Func<VoiceSession?> _voice = () => null;
    private readonly Func<string?> _output = () => null;
    private readonly Func<string, bool> _isPersonMuted = _ => false;
    private readonly Action<string, bool> _setPersonMuted = (_, _) => { };
    private readonly ObservableCollection<SoundVm> _tiles = new();
    private bool _loading;

    public SoundboardWindow() => InitializeComponent();

    internal SoundboardWindow(ChatRoom room, PeerId me, AppSettings settings, Action save, Func<VoiceSession?> voice, Func<string?> output,
        Func<string, bool> isPersonMuted, Action<string, bool> setPersonMuted) : this()
    {
        _room = room; _me = me; _settings = settings; _save = save; _voice = voice; _output = output;
        _isPersonMuted = isPersonMuted; _setPersonMuted = setPersonMuted;
        Title = $"Soundboard - {room.Name}";
        Tiles.ItemsSource = _tiles;

        _loading = true;
        MuteAllBox.IsChecked = settings.SoundboardsMuted;
        VolumeBox.Value = settings.SoundboardVolume;
        _loading = false;

        room.SoundsChanged += OnSoundsChanged;
        room.SoundPlayed += OnSoundPlayed;
        Closed += (_, _) =>
        {
            room.SoundsChanged -= OnSoundsChanged;
            room.SoundPlayed -= OnSoundPlayed;
        };
        Reload();
    }

    private void OnSoundsChanged() => Dispatcher.UIThread.Post(Reload);

    private void OnSoundPlayed(PeerId by, SoundInfo sound) => Dispatcher.UIThread.Post(() =>
    {
        var who = by == _me ? "You" : _room.VoiceParticipants.FirstOrDefault(p => p.Peer.Id == by)?.Peer.DisplayName ?? "Someone";
        Footer.Text = $"{who} played {sound.Name}";
    });

    private void Reload()
    {
        _tiles.Clear();
        foreach (var s in _room.Sounds) _tiles.Add(new SoundVm(s, s.Owner == _me));
        EmptyText.IsVisible = _tiles.Count == 0;
    }

    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SoundVm tile }) return;
        var voice = _voice();
        if (voice == null) Footer.Text = "Join voice to play sounds.";
        else if (voice.Deafened) Footer.Text = "You can't play sounds while deafened.";
        else if (!_room.PlaySound(tile.Id)) Footer.Text = "That sound hasn't finished downloading yet.";
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
            string owner = tile.Info.Owner.Value;
            bool muted = _isPersonMuted(owner);
            var mute = new MenuItem { Header = muted ? $"Unmute {tile.Info.OwnerName}'s soundboard" : $"Mute {tile.Info.OwnerName}'s soundboard" };
            mute.Click += (_, _) => _setPersonMuted(owner, !muted);
            items.Add(new Separator());
            items.Add(mute);
        }
        new ContextMenu { ItemsSource = items }.Open(button);
        e.Handled = true;
    }

    private void Preview(SoundVm tile)
    {
        if (_room.GetSound(tile.Id) is not { } clip) return;
        try { VoiceAudio.PlayOnce(clip.ToSamples(), _output()); }
        catch (Exception ex) { Footer.Text = "Couldn't play the sound: " + ex.Message; }
    }

    private async Task RenameAsync(SoundVm tile)
    {
        var name = await PromptDialog.AskAsync(this, "New name for the sound:", tile.Name);
        if (string.IsNullOrWhiteSpace(name) || name == tile.Name) return;
        try { _room.RenameSound(tile.Id, name); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { Footer.Text = ex.Message; }
    }

    private async Task RemoveAsync(SoundVm tile)
    {
        if (!await Dialogs.ConfirmAsync(this, "Delete sound", $"Remove \"{tile.Name}\" from the soundboard for everyone?", "Delete", "Keep")) return;
        try { _room.RemoveSound(tile.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { Footer.Text = ex.Message; }
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AddSoundDialog(_output);
        if (!await dialog.ShowDialog<bool>(this) || dialog.Clip == null || dialog.SoundName == null) return;
        try
        {
            _room.AddSound(dialog.SoundName, dialog.Clip);
            Footer.Text = $"Added {dialog.SoundName}.";
        }
        catch (ArgumentException ex) { Footer.Text = ex.Message; }
    }

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
