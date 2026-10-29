using Avalonia.Interactivity;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Blocking someone: from the People list or from one of their messages. The list is kept in the settings.
public partial class MainWindow
{
    private string? BlockedIdOf(object? item) => item switch
    {
        PeerVm { Id: var id } when id != _node?.Id.Value => id,
        MessageVm { IsMine: false, Source: { } source } => source.From.Id.Value,
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
        BlockPersonItem.IsVisible = !person.Blocked;
        UnblockPersonItem.IsVisible = person.Blocked;
    }

    private void MessageMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var message = MessageList.SelectedItem as MessageVm;
        RetryItem.IsVisible = message is { CanRetry: true };
        BlockSenderItem.IsVisible = message != null && BlockedIdOf(message) is { } id && _node?.IsBlocked(new PeerId(id)) == false;
        if (!RetryItem.IsVisible && !BlockSenderItem.IsVisible) e.Cancel = true;
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
        RefreshPeers();
        foreach (var conv in _roomConversations.Values) conv.VoiceMembersChanged();
        foreach (var (room, conv) in _roomConversations) RefreshVoice(conv, room, announce: false);
    }
}
