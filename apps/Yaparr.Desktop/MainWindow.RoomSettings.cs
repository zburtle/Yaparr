using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// A room's settings in a panel over the window, like the app's settings: its name (only this person sees it), its key (to share, and for the owner to
// replace), its GIF shelf (adding, removing and taking down; the GIF button only sends from it) and its soundboard (adding a sound; the soundboard plays them).
public partial class MainWindow
{
    private ChatRoom? _roomSettingsRoom;
    private IInputElement? _roomSettingsOpener;
    private int _roomShelfVersion;

    private void RoomSettings_Click(object? sender, RoutedEventArgs e) => ShowRoomSettings();

    // Opens the room settings for the room on show, on a section: "name", "key" or "shelf".
    internal void ShowRoomSettings(string section = "name")
    {
        if (OpenRoom() is not var (conv, room)) return;
        if (!RoomSettingsOverlay.IsVisible) _roomSettingsOpener = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        _roomSettingsRoom = room;
        RoomSettingsName.Text = RoomLabel(conv);
        RoomNameBox.Text = RoomLabel(conv);
        RoomNameSave.IsEnabled = false;
        ShowRoomKey(conv);
        RoomSettingsOverlay.IsVisible = true;
        var item = RoomSettingsList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == section) ?? RoomSettingsList.Items.OfType<ListBoxItem>().First();
        if (RoomSettingsList.SelectedItem == item) ShowRoomSection(section); else RoomSettingsList.SelectedItem = item;
        Dispatcher.UIThread.Post(() => (section == "name" ? (Control)RoomNameBox : item).Focus());
    }

    private void CloseRoomSettings()
    {
        if (!RoomSettingsOverlay.IsVisible) return;
        RoomSettingsOverlay.IsVisible = false;
        StopWatchingRoomShelf();
        StopWatchingRoomSounds();
        _roomSettingsRoom = null;
        (_roomSettingsOpener as Control)?.Focus();
        _roomSettingsOpener = null;
    }

    private void CloseRoomSettings_Click(object? sender, RoutedEventArgs e) => CloseRoomSettings();

    // A click on the dark area around the panel puts it away; a click on the panel itself does not reach it.
    private void RoomSettingsOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == RoomSettingsOverlay) CloseRoomSettings();
    }

    private void RoomSettingsOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseRoomSettings();
        e.Handled = true;
    }

    private void RoomSettingsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RoomSettingsList.SelectedItem is ListBoxItem { Tag: string key }) ShowRoomSection(key);
    }

    private void ShowRoomSection(string key)
    {
        RoomSectionName.IsVisible = key == "name";
        RoomSectionKey.IsVisible = key == "key";
        RoomSectionShelf.IsVisible = key == "shelf";
        RoomSectionSounds.IsVisible = key == "sounds";
        if (key == "shelf") WatchRoomShelf(); else StopWatchingRoomShelf();
        if (key == "sounds") WatchRoomSounds(); else StopWatchingRoomSounds();
    }

    // ---- Name ----

    private void RoomNameBox_TextChanged(object? sender, TextChangedEventArgs e) =>
        RoomNameSave.IsEnabled = _selected is { IsRoom: true } conv && !string.IsNullOrWhiteSpace(RoomNameBox.Text) && RoomNameBox.Text.Trim() != RoomLabel(conv);

    private void RoomNameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveRoomName();
        e.Handled = true;
    }

    private void RoomNameSave_Click(object? sender, RoutedEventArgs e) => SaveRoomName();

    private void SaveRoomName()
    {
        if (!RoomNameSave.IsEnabled || _selected is not { IsRoom: true } conv) return;
        RenameRoom(conv, RoomNameBox.Text ?? "");
        RoomSettingsName.Text = RoomLabel(conv);
        RoomNameSave.IsEnabled = false;
        StatusText.Text = $"This room is now called {RoomLabel(conv)} for you.";
    }

    // ---- Key ----

    private void ShowRoomKey(ConversationVm conv, string? invite = null)
    {
        invite ??= conv.Invite;
        bool has = !string.IsNullOrEmpty(invite);
        RoomKeyShown.IsVisible = has;
        RoomKeyNone.IsVisible = !has;
        RoomKeyText.Text = invite ?? "";
        bool owner = conv.IsOwner;
        RoomKeyReplace.IsVisible = has;
        RoomKeyReplaceButton.IsVisible = owner;
        RoomKeyReplaceNote.Text = owner
            ? "Gives the room a new key and hands it to everyone Yaparr has seen in it. Banned people, and anyone who only kept the old invite, can no longer find the room or talk in it."
            : "Only the person who made the room can replace its key.";
    }

    // ---- GIF shelf ----

    private void WatchRoomShelf()
    {
        if (_roomSettingsRoom is not { } room) return;
        room.GifsChanged -= OnRoomShelfChanged;
        room.GifsChanged += OnRoomShelfChanged;
        room.RequestGifs();
        _ = FillRoomShelfAsync();
    }

    private void StopWatchingRoomShelf()
    {
        if (_roomSettingsRoom != null) _roomSettingsRoom.GifsChanged -= OnRoomShelfChanged;
        _roomShelfVersion++;
    }

    private void OnRoomShelfChanged() => Dispatcher.UIThread.Post(async () =>
    {
        if (RoomSettingsOverlay.IsVisible && RoomSectionShelf.IsVisible) await FillRoomShelfAsync();
    });

    private async Task FillRoomShelfAsync()
    {
        if (_roomSettingsRoom is not { } room) return;
        int version = ++_roomShelfVersion;
        await FillRoomTilesAsync(room, RoomShelfGrid, RoomShelfEmpty, RoomShelfCount, () => version != _roomShelfVersion, editing: true);
    }

    // ---- Soundboard ----

    private void WatchRoomSounds()
    {
        if (_roomSettingsRoom is not { } room) return;
        room.SoundsChanged -= OnRoomSoundsChanged;
        room.SoundsChanged += OnRoomSoundsChanged;
        ShowRoomSounds();
    }

    private void StopWatchingRoomSounds()
    {
        if (_roomSettingsRoom != null) _roomSettingsRoom.SoundsChanged -= OnRoomSoundsChanged;
    }

    private void OnRoomSoundsChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (RoomSettingsOverlay.IsVisible && RoomSectionSounds.IsVisible) ShowRoomSounds();
    });

    // The sounds of the room, each with what this person may do to it: hear it, and (their own) rename or delete it; an admin may rename or delete any.
    private void ShowRoomSounds()
    {
        if (_roomSettingsRoom is not { } room || _node == null) return;
        var me = _node.Id;
        bool admin = room.MyRole >= RoomRole.Admin;
        var sounds = room.Sounds;
        RoomSoundList.ItemsSource = sounds.Select(s => new SoundVm(s, s.Owner == me) { CanDelete = s.Owner == me || admin, CanRename = s.Owner == me || admin }).ToList();
        RoomSoundCount.Text = sounds.Count == 0 ? "No sounds yet." : sounds.Count == 1 ? "1 sound" : sounds.Count + " sounds";
    }

    private void RoomSoundPreview_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SoundVm tile } || _roomSettingsRoom?.GetSound(tile.Id) is not { } clip) return;
        try { VoiceAudio.PlayOnce(clip.ToSamples(), _settings.VoiceOutput); }
        catch (Exception ex) { StatusText.Text = "Couldn't play the sound: " + ex.Message; }
    }

    private async void RoomSoundRename_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SoundVm tile } || _roomSettingsRoom is not { } room) return;
        var name = await PromptDialog.AskAsync(this, "New name for the sound:", tile.Name);
        if (string.IsNullOrWhiteSpace(name) || name == tile.Name) return;
        try { room.RenameSound(tile.Id, name); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
    }

    private async void RoomSoundDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SoundVm tile } || _roomSettingsRoom is not { } room) return;
        var who = tile.Mine ? "" : $" It was added by {tile.Info.OwnerName}.";
        if (!await Dialogs.ConfirmAsync(this, "Delete sound", $"Remove \"{tile.Name}\" from the soundboard for everyone?{who}", "Delete", "Keep")) return;
        try { if (tile.Mine) room.RemoveSound(tile.Id); else room.DeleteSound(tile.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
    }

    private async void RoomSoundAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (_roomSettingsRoom is not { } room) return;
        var dialog = new AddSoundDialog(() => _settings.VoiceOutput);
        if (!await dialog.ShowDialog<bool>(this) || dialog.NewSound == null || dialog.SoundName == null) return;
        try
        {
            room.AddSound(dialog.SoundName, dialog.NewSound);
            StatusText.Text = $"Added {dialog.SoundName} to the soundboard.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { StatusText.Text = ex.Message; }
        ShowRoomSounds();
    }

    private async void RoomShelfAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (_roomSettingsRoom is not { } room) return;
        await AddPicturesAsync("Add to the room's GIF shelf", bytes => room.AddGif(bytes));
        await FillRoomShelfAsync();
    }
}
