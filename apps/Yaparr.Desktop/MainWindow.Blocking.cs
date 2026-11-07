using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Blocking someone: from the People list or from one of their messages. The list is kept in the settings.
public partial class MainWindow
{
    private string? BlockedIdOf(object? item) => item switch
    {
        PeerVm { Id: var id } when id != _node?.AccountId.Value => id,
        MessageVm { IsMine: false, Source: { } source } => source.From.Account.Value,
        _ => null,
    };

    private string NameOf(object? item) => item switch
    {
        PeerVm p => p.Name,
        MessageVm { Source: { } source } => source.From.DisplayName,
        _ => "this person",
    };

    private void PeopleMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (PeopleList.SelectedItem is not PeerVm person || BlockedIdOf(person) == null) { e.Cancel = true; return; }
        DirectChatPersonItem.IsVisible = !person.Blocked;
        BlockPersonItem.IsVisible = !person.Blocked;
        UnblockPersonItem.IsVisible = person.Blocked;
        bool canChange = CanChangeAdmin(person, out _);
        MakeAdminItem.IsVisible = canChange && person.Role == RoomRole.Member;
        RemoveAdminItem.IsVisible = canChange && person.Role == RoomRole.Admin;
        BanPersonItem.IsVisible = CanBan(person.Id, person.Role) && !person.BannedHere;
        UnbanPersonItem.IsVisible = SelectedRoom is { MyRole: >= RoomRole.Admin } && person.BannedHere;
    }

    private void MessageMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var message = MessageList.SelectedItem as MessageVm;
        ReplyItem.IsVisible = ReactItem.IsVisible = message is { Key: not null, IsSystem: false };
        SavePictureItem.IsVisible = AddToShelfItem.IsVisible = message is { Image: not null };
        RetryItem.IsVisible = message is { CanRetry: true };
        BlockSenderItem.IsVisible = message != null && BlockedIdOf(message) is { } id && _node?.IsBlocked(new PeerId(id)) == false;
        var room = SelectedRoom;
        var source = message?.Source;
        DeleteMessageItem.IsVisible = room is { MyRole: >= RoomRole.Admin } && source != null;
        BanSenderItem.IsVisible = source != null && !message!.IsMine && CanBan(source.From.Account.Value, room?.RoleOf(source.From.Account) ?? RoomRole.Member) && room?.IsBanned(source.From.Account) == false;
        if (!ReplyItem.IsVisible && !SavePictureItem.IsVisible && !RetryItem.IsVisible && !BlockSenderItem.IsVisible && !DeleteMessageItem.IsVisible && !BanSenderItem.IsVisible) e.Cancel = true;
    }

    // Everyone blocked, by name where it is known, so that someone met only in a direct chat can be unblocked too.
    private void RefreshBlocked()
    {
        if (_node == null) return;
        var rows = _node.BlockedPeers
            .Select(p => new BlockedRow(p.Value,
                _settings.BlockedPeers.FirstOrDefault(b => b.Id == p.Value)?.Name is { Length: > 0 } saved ? saved
                    : _settings.KnownPeers.FirstOrDefault(k => k.Id == p.Value)?.Name is { Length: > 0 } known ? known : "Unknown name"))
            .OrderBy(r => r.Name == "Unknown name").ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        BlockedList.ItemsSource = rows;
        BlockedEmpty.IsVisible = rows.Count == 0;
    }

    private void Unblock_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || (sender as Avalonia.Controls.Control)?.DataContext is not BlockedRow row) return;
        _node.Unblock(new PeerId(row.Id));
        RefreshBlocked();
    }

    private async void BlockPerson_Click(object? sender, RoutedEventArgs e) => await BlockAsync(PeopleList.SelectedItem);

    private async void BlockSender_Click(object? sender, RoutedEventArgs e) => await BlockAsync(MessageList.SelectedItem);

    private void UnblockPerson_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || PeopleList.SelectedItem is not PeerVm { Blocked: true } person) return;
        _node.Unblock(new PeerId(person.Id));
        StatusText.Text = $"Unblocked {person.Name}.";
    }

    private async Task BlockAsync(object? item)
    {
        if (_node == null || BlockedIdOf(item) is not { } id) return;
        var name = NameOf(item);
        var proceed = await Dialogs.ConfirmAsync(this, "Block " + name,
            $"Block {name}? You will no longer see their messages or hear them, they disappear from your lists, and your connection to them is closed. They are not told, and everyone else still sees them. You can unblock them from the People list.");
        if (!proceed) return;

        if (_settings.BlockedPeers.All(b => b.Id != id))
            _settings.BlockedPeers.Add(new KnownPeer { Id = id, Name = name, LastSeen = DateTimeOffset.Now });
        _data.SaveSettings(_settings);
        _node.Block(new PeerId(id));
        StatusText.Text = $"Blocked {name}.";
    }

    // The node reports every change, so the saved list and the People list follow it.
    private void OnBlockChanged(PeerId id, bool blocked)
    {
        if (!blocked && _settings.BlockedPeers.RemoveAll(b => b.Id == id.Value) > 0) _data.SaveSettings(_settings);
        // Blocked on another of your devices: the list kept here follows.
        if (blocked && _settings.BlockedPeers.All(b => b.Id != id.Value))
        {
            var name = _settings.KnownPeers.FirstOrDefault(k => k.Id == id.Value)?.Name ?? "";
            _settings.BlockedPeers.Add(new KnownPeer { Id = id.Value, Name = name, LastSeen = DateTimeOffset.Now });
            _data.SaveSettings(_settings);
        }
        RefreshPeers();
        if (SettingsOverlay.IsVisible) RefreshBlocked();
        foreach (var conv in _roomConversations.Values) conv.VoiceMembersChanged();
        foreach (var (room, conv) in _roomConversations) RefreshVoice(conv, room, announce: false);
    }
}

/// <summary>One line of the blocked people in the settings.</summary>
internal sealed record BlockedRow(string Id, string Name)
{
    public string ShortId => Id.Length > 12 ? Id[..12] + "…" : Id;
}
