using Yaparr.Chat;

namespace Yaparr.Desktop;

// Replacing a room's key. The owner does it to lock out anyone who was banned; everyone else follows when the new key reaches them.
public partial class MainWindow
{
    private static string? RoomIdOf(string invite)
    {
        try { return RoomKey.Parse(invite).RoomId; }
        catch (FormatException) { return null; }
    }

    private static int EpochOf(string invite)
    {
        try { return RoomKey.Parse(invite).Epoch; }
        catch (FormatException) { return 0; }
    }

    // The saved invite and the one shown follow the room's key.
    private void OnSecretChanged(ConversationVm conv, string before, string after)
    {
        if (_settings.PrivateRooms.FirstOrDefault(r => r.Key == before) is { } saved)
        {
            saved.Key = after;
            _data.SaveSettings(_settings);
        }
        conv.Invite = after;
        conv.Add(MessageVm.System("The room's key was replaced. The old invite no longer works; use \"Copy invite\" to share the new one."));
    }

    private async void RotateKey_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (OpenRoom() is not var (conv, room) || room.MyRole != RoomRole.Owner) return;
        var people = room.RotationCandidates;
        int away = people.Count(p => !p.Online);
        var names = people.Count == 0 ? "nobody else has been here" : string.Join(", ", people.Take(15).Select(p => p.Name)) + (people.Count > 15 ? $" and {people.Count - 15} more" : "");
        var proceed = await Dialogs.ConfirmAsync(this, "Replace the room key",
            $"Give the room a new key and hand it to everyone Yaparr has seen in it ({people.Count}): {names}.\n\n"
            + (away > 0 ? $"{away} of them are away now; they get the key when they come back online within about a month. " : "")
            + "Banned people and anyone not on this list can no longer find the room or talk in it, even if they kept the old invite. If someone on the list should not have the new key, ban them first.\n\nReplace the key?",
            "Replace", "Cancel");
        if (!proceed) return;
        try
        {
            var invite = room.RotateSecret(people.Select(p => p.Account).ToList());
            if (RoomSettingsOverlay.IsVisible) ShowRoomKey(conv, invite);
            var copied = await Dialogs.CopyAsync(this, invite);
            StatusText.Text = copied
                ? $"The room has a new key, for {people.Count} {(people.Count == 1 ? "person" : "people")}. The new invite was copied; send it to anyone new, or anyone away for more than a month."
                : $"The room has a new key, for {people.Count} {(people.Count == 1 ? "person" : "people")}. Use \"Copy invite\" to send it to anyone new, or anyone away for more than a month.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { StatusText.Text = ex.Message; }
    }
}
