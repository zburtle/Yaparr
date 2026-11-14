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

    // The owner of the room removed this person from it: the room goes from the list, as when they leave it themselves, and they are told. Nothing keeps them
    // out; they come back with the invite.
    private async Task OnRemovedFromRoomAsync(ConversationVm conv)
    {
        try
        {
            if (_voiceConv == conv) LeaveVoiceCore();
            string title = conv.Title;
            await LeaveConversationAsync(conv);
            StatusText.Text = $"The owner removed you from {title}.";
            await Dialogs.MessageAsync(this, "Removed from room", $"The owner of \"{title}\" removed you from the room. You are not banned: to come back, open the room's invite again.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { StatusText.Text = "Removed from the room, but leaving it did not finish cleanly: " + ex.Message; }
    }

    // The owner takes the person selected in the People list out of the open room, without banning them.
    private async void RemoveFromRoom_Click(object? sender, RoutedEventArgs e)
    {
        if (PeopleList.SelectedItem is not PeerVm person || SelectedRoom is not { } room) return;
        var proceed = await Dialogs.ConfirmAsync(this, "Remove " + person.Name,
            $"Remove {person.Name} from this room? They disappear from everyone's list of people, and their Yaparr leaves the room if they are on now. This is not a ban: nothing keeps them out, and they are back as soon as they open the room's invite again. To keep someone out, ban them.",
            "Remove", "Cancel");
        if (!proceed) return;
        try
        {
            room.RemoveFromRoom(new PeerId(person.Id), person.Name);
            StatusText.Text = $"Removed {person.Name} from this room.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { StatusText.Text = ex.Message; }
    }

    // The owner takes someone out of the voice channel, without banning them. The right-click menu of a person in the voice list.
    private async void KickFromVoice_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Avalonia.Controls.Control)?.DataContext is not VoiceMemberVm member) return;
        var room = _roomConversations.FirstOrDefault(kv => kv.Value.VoiceMembers.Contains(member)).Key;
        if (room == null) return;
        var proceed = await Dialogs.ConfirmAsync(this, "Kick " + member.Name,
            $"Kick {member.Name} from voice? Their call ends and they are taken off the list of people in voice. This is not a ban: they stay in the room and can join voice again whenever they like.",
            "Kick", "Cancel");
        if (!proceed) return;
        try
        {
            room.KickFromVoice(new PeerId(member.Account.Length > 0 ? member.Account : member.Id));
            StatusText.Text = $"Kicked {member.Name} from voice.";
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
        if (MessageList.SelectedItem is not MessageVm { Key: { } key } || SelectedRoom is not { } room) return;
        var proceed = await Dialogs.ConfirmAsync(this, "Delete message", "Delete this message for everyone in the room? It disappears from every member's history and cannot be brought back.", "Delete", "Keep");
        if (!proceed) return;
        try { room.DeleteMessage(key); }
        catch (Exception ex) when (ex is InvalidOperationException) { StatusText.Text = ex.Message; }
    }
}
