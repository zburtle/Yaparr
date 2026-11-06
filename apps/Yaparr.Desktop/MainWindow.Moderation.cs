using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Bans and deletions by the owner and admins of the room that is open. Everyone's app applies them; see ChatRoom.Ban.
public partial class MainWindow
{
    // Admins and the owner can ban an ordinary member of the open room, other than themselves.
    private bool CanBan(string accountId, RoomRole role) =>
        SelectedRoom is { MyRole: >= RoomRole.Admin } && role == RoomRole.Member && accountId != _node?.AccountId.Value;

    private async void BanPerson_Click(object? sender, RoutedEventArgs e)
    {
        if (PeopleList.SelectedItem is PeerVm person) await BanAsync(person.Id, person.Name);
    }

    private async void BanSender_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageVm { Source: { } source }) await BanAsync(source.From.Account.Value, source.From.DisplayName);
    }

    private async Task BanAsync(string accountId, string name)
    {
        if (SelectedRoom is not { } room) return;
        var proceed = await Dialogs.ConfirmAsync(this, "Ban " + name,
            $"Ban {name} from this room? Everyone's Yaparr will stop showing their messages, voice and sounds, and stop sending them anything. They still hold the room's invite, so this does not lock them out: they can come back under a new identity. You can unban them later from the People list.",
            "Ban", "Cancel");
        if (!proceed) return;
        try
        {
            room.Ban(new PeerId(accountId), name);
            StatusText.Text = $"Banned {name} from this room.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { StatusText.Text = ex.Message; }
    }

    private void UnbanPerson_Click(object? sender, RoutedEventArgs e)
    {
        if (PeopleList.SelectedItem is not PeerVm { BannedHere: true } person || SelectedRoom is not { } room) return;
        try
        {
            room.Unban(new PeerId(person.Id));
            StatusText.Text = $"Unbanned {person.Name}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { StatusText.Text = ex.Message; }
    }

    private async void DeleteMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageVm { Source: { } source } || SelectedRoom is not { } room) return;
        var proceed = await Dialogs.ConfirmAsync(this, "Delete message", "Delete this message for everyone in the room? It disappears from every member's history and cannot be brought back.", "Delete", "Keep");
        if (!proceed) return;
        try { room.DeleteMessage(source); }
        catch (Exception ex) when (ex is InvalidOperationException) { StatusText.Text = ex.Message; }
    }
}
