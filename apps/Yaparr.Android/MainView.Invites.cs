using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

// Inviting someone to a room, and answering an invitation (see RoomInvite). An invitation is a message in a direct chat; receiving one joins nothing until the person says so.
public partial class MainView
{
    // The rooms to choose from, in the same sheet the chat menu uses. Nothing is offered when there is no room to invite to.
    private void ChooseRoomToInvite(Chat? except, Func<Chat, Task> then)
    {
        if (_session is not { } session) return;
        var rooms = session.RoomsToInviteTo(except);
        if (rooms.Count == 0)
        {
            if (_current is { } here) ShowChatNote(here, except != null ? "You are not in another room to invite them to" : "You are not in a room yet");
            return;
        }
        ActionTitle.Text = "Invite to which room?";
        ActionList.Children.Clear();
        foreach (var room in rooms)
        {
            var chosen = room;
            AddAction(chosen.Title, () => _ = then(chosen));
        }
        ActionPanel.IsVisible = true;
    }

    // Sends the key of the room to the person, in their direct chat, and says so where you are.
    private async Task InviteAsync(Chat direct, string name, Chat room, Chat? noteIn = null)
    {
        if (_session is not { } session) return;
        var here = noteIn ?? _current ?? direct;
        try
        {
            await session.SendInviteAsync(direct, room);
            ShowChatNote(here, $"Invited {name} to \"{room.Title}\"");
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException) { ShowChatNote(here, "Couldn't send the invitation: " + ex.Message); }
    }

    // Joins the room an invitation is for, after asking. The room is named as its sender calls it (it can be renamed after).
    private void AcceptInvite(RoomInvite invite, string sender, Action<bool>? done = null)
    {
        if (_session is not { } session) return;
        try { RoomKey.Parse(invite.Key); }
        catch (FormatException) { ListStatus.Text = "That invitation is not valid."; done?.Invoke(false); return; }
        Ask($"{sender} invited you to \"{invite.Name}\".\n\nOnce you join, the other members can see your IP address, and anyone with the invite can join too. Only join if you trust them.", "Join", async () =>
        {
            try
            {
                var chat = await session.JoinWithInviteAsync(invite.Name, invite.Key);
                MarkInvitesJoined(invite.Key);
                done?.Invoke(true);
                for (int i = 0; AnyOverlay && i < 20 && GoBack(); i++) { }
                if (ChatPanel.IsVisible) BackToList();
                OpenChat(chat);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or OperationCanceledException)
            {
                ListStatus.Text = "Couldn't join: " + ex.Message;
                done?.Invoke(false);
            }
        }, declined: () => done?.Invoke(false));
    }

    // The invitations waiting for this room are answered by joining it.
    private void MarkInvitesJoined(string inviteKey)
    {
        if (_session is not { } session) return;
        string? room = null;
        try { room = RoomKey.Parse(inviteKey).RoomId; } catch (FormatException) { }
        foreach (var entry in session.Notifications.OpenInvites.ToList())
        {
            bool same = entry.Invite == inviteKey;
            if (!same && room != null) { try { same = RoomKey.Parse(entry.Invite!).RoomId == room; } catch (FormatException) { } }
            if (same) session.Notifications.SetOutcome(entry.Id, NotificationOutcomes.Joined);
        }
    }

    // The Join button under an invitation in a chat.
    private void InviteJoinClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not Line { Invite: { } invite } line) return;
        e.Handled = true;
        AcceptInvite(invite, line.Who);
    }

    private void NotificationJoinClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.Tag is not string id || _session?.Notifications.Items.FirstOrDefault(n => n.Id == id) is not { IsOpenInvite: true } entry) return;
        AcceptInvite(new RoomInvite(entry.Invite!, entry.Title), entry.Sender);
    }

    private void NotificationDeclineClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.Tag is string id) _session?.Notifications.SetOutcome(id, NotificationOutcomes.Declined);
    }
}
