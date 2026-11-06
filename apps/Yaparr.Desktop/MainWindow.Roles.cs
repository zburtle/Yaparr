using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Admins of a room. The owner (named in the invite) appoints and removes them from the People list; everybody sees the roles.
public partial class MainWindow
{
    // The room that is open, if a room is.
    private ChatRoom? SelectedRoom => _selected is { IsRoom: true } conv ? _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key : null;

    // Only the owner appoints admins, and only of someone else.
    private bool CanChangeAdmin(PeerVm? person, out ChatRoom? room)
    {
        room = SelectedRoom;
        return room is { MyRole: RoomRole.Owner } && person != null && !person.Blocked && person.Id != _node?.AccountId.Value
            && room.RoleOf(new PeerId(person.Id)) != RoomRole.Owner;
    }

    private void MakeAdmin_Click(object? sender, RoutedEventArgs e) => ChangeAdmin(admin: true);

    private void RemoveAdmin_Click(object? sender, RoutedEventArgs e) => ChangeAdmin(admin: false);

    private void ChangeAdmin(bool admin)
    {
        if (PeopleList.SelectedItem is not PeerVm person || !CanChangeAdmin(person, out var room) || room == null) return;
        try
        {
            room.SetAdmin(new PeerId(person.Id), admin);
            StatusText.Text = admin ? $"{person.Name} is now an admin of this room." : $"{person.Name} is no longer an admin of this room.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusText.Text = ex.Message;
        }
    }
}
