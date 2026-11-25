using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Inviting someone to a room, and answering an invitation (see RoomInvite). An invitation is a message in a direct chat; receiving one joins nothing until the person says so.
public partial class MainWindow
{
    // From a direct chat in the list.
    private async void InviteDirect_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || _selected is not { IsRoom: false, IsNotes: false, PeerId: { } peer }) return;
        var id = new PeerId(peer);
        await InviteToRoomAsync(id, _node.GetDirectChat(id).DisplayName, except: null);
    }

    // From the people of a room: to one of your other rooms.
    private async void InvitePerson_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || PeopleList.SelectedItem is not PeerVm person) return;
        await InviteToRoomAsync(new PeerId(person.Id), person.Display, except: _selected is { IsRoom: true } open ? open : null);
    }

    /// <summary>Asks which room, then sends the person its key in a direct chat (opened if there is none). They choose whether to join.</summary>
    private async Task InviteToRoomAsync(PeerId person, string display, ConversationVm? except)
    {
        if (_node == null) return;
        if (person.Value == _node.AccountId.Value) { StatusText.Text = "That is you."; return; }
        if (_node.IsBlocked(person)) { StatusText.Text = $"You have blocked {display}."; return; }
        var rooms = _conversations.Where(c => c.IsRoom && c.Invite != null && c != except).ToList();
        if (rooms.Count == 0)
        {
            await Dialogs.MessageAsync(this, "Invite to a room", except != null ? "You are not in another room to invite them to." : "You are not in a room yet. Make one first.");
            return;
        }
        var pick = await Dialogs.PickAsync(this, "Invite to a room",
            $"Which room should {display} be invited to?\n\nThey are sent the room's key in a direct chat and choose whether to join. Anyone who has the key can join, so invite only people you trust.",
            rooms.Select(RoomLabel).ToList(), "Invite");
        if (pick is not { } index || _node == null) return;
        var room = rooms[index];

        ConversationVm? chat;
        lock (_directConversations) _directConversations.TryGetValue(person, out chat);
        if (chat == null)
        {
            await OpenDirectAsync(person);
            lock (_directConversations) _directConversations.TryGetValue(person, out chat);
        }
        if (chat == null) { StatusText.Text = $"Couldn't open a chat with {display}, so the invitation was not sent."; return; }

        var invite = new RoomInvite(room.Invite!, RoomInvite.CleanName(RoomLabel(room)));
        try
        {
            var id = await chat.Send(invite.ToText());
            chat.Add(new MessageVm(DateTimeOffset.Now, _node.DisplayName, "", isMine: true, id: id) { Invite = invite, Key = ChatMessage.KeyOf(_node.Id, id) });
            StatusText.Text = $"Invited {display} to \"{invite.Name}\".";
        }
        catch (InvalidOperationException ex) { StatusText.Text = "Couldn't send the invitation: " + ex.Message; }
    }

    // An invitation that came in live is listed in the notifications, with Join and Decline. (One that arrived while this computer was off shows in its chat, like any message that was caught up.)
    private void RecordInvite(ConversationVm conv, MessageVm message)
    {
        if (message.Invite is not { } invite || NotificationKeyOf(conv) is not { Length: > 0 } chat) return;
        if (_settings.PrivateRooms.Any(r => r.Key == invite.Key)) return;   // already in that room, with that key
        _notificationLog.Add(new NotificationEntry
        {
            Kind = NotificationKinds.Invite,
            Time = message.Time.ToUnixTimeMilliseconds(),
            ChatKey = chat,
            IsRoom = false,
            Title = invite.Name,
            Sender = message.Sender,
            MessageKey = message.Key,
            Invite = invite.Key,
            Read = conv == _selected && IsActive,
        });
    }

    // Joins the room an invitation is for, after asking. True when you are in it afterwards.
    private async Task<bool> AcceptInviteAsync(RoomInvite invite, string sender)
    {
        RoomKey key;
        try { key = RoomKey.Parse(invite.Key); }
        catch (FormatException) { StatusText.Text = "That invitation is not valid."; return false; }
        return await JoinWithKeyAsync(key, fromLink: true, invitedBy: sender, roomName: invite.Name);
    }

    // The invitations waiting for this room are answered by joining it.
    private void MarkInvitesJoined(string inviteKey)
    {
        string? room = null;
        try { room = RoomKey.Parse(inviteKey).RoomId; } catch (FormatException) { }
        foreach (var entry in _notificationLog.OpenInvites.ToList())
        {
            bool same = entry.Invite == inviteKey;
            if (!same && room != null) { try { same = RoomKey.Parse(entry.Invite!).RoomId == room; } catch (FormatException) { } }
            if (same) _notificationLog.SetOutcome(entry.Id, NotificationOutcomes.Joined);
        }
    }

    // The Join button under an invitation in a chat.
    private async void InviteJoin_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MessageVm { Invite: { } invite } message) return;
        if (await AcceptInviteAsync(invite, message.Sender)) MarkInvitesJoined(invite.Key);
    }

    private async void NotificationJoin_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.Tag is not string id || _notificationLog.Items.FirstOrDefault(n => n.Id == id) is not { IsOpenInvite: true } entry) return;
        if (!await AcceptInviteAsync(new RoomInvite(entry.Invite!, entry.Title), entry.Sender)) return;
        MarkInvitesJoined(entry.Invite!);
        CloseNotificationList();
    }

    private void NotificationDecline_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.Tag is string id) _notificationLog.SetOutcome(id, NotificationOutcomes.Declined);
    }
}
