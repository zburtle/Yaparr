using Yaparr.Chat;

namespace Yaparr.Desktop;

// What a room has not shown you, kept across restarts: for each text channel the time up to which you have seen it, and the channel that
// was on show in each room. Both are kept in the settings, by the room's permanent id.
public partial class MainWindow
{
    private ReadMarks? _marks;
    private ReadMarks Marks => _marks ??= new ReadMarks(_settings.ReadMarks);

    // Settings and the choice of channel are written with the next beat of the timer.
    private bool _channelChoiceDirty;

    // While the rooms are being brought back at the start, opening each of them is not you reading it.
    private bool _restoringRooms;

    private ChatRoom? RoomFor(ConversationVm conv) => _roomConversations.FirstOrDefault(kv => kv.Value == conv).Key;

    private void MarkChannelRead(ChatRoom room, string channel, long? at = null) => Marks.Mark(room.RoomId, channel, at);

    // The channel on show in this room is read, as of now.
    private void MarkShownRead(ConversationVm? conv)
    {
        if (conv is { IsRoom: true } && RoomFor(conv) is { } room) MarkChannelRead(room, conv.CurrentChannel);
    }

    // Written with the next beat of the timer and when the app closes, not with every message.
    private void SaveMarks()
    {
        if (!Marks.Dirty && !_channelChoiceDirty) return;
        Marks.Saved();
        _channelChoiceDirty = false;
        try { _data.SaveSettings(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("could not save what you have read: " + ex.Message); }
    }

    // A message of the history that arrived while the app was shut counts as unread when it is newer than what was seen in its channel.
    private void CountIfUnread(ConversationVm conv, ChatRoom room, MessageVm message)
    {
        if (message.IsMine || message.Channel is not { } channel) return;
        if (Marks.IsUnread(room.RoomId, channel, message.Time.ToUnixTimeMilliseconds())) conv.NoteUnread(channel, message.Highlight);
    }

    // The channel that was on show when the app was last used comes back, once the room's channels are known.
    private void RememberChannel(ChatRoom room, string channel)
    {
        if (_settings.RoomChannels.TryGetValue(room.RoomId, out var have) && have == channel) return;
        _settings.RoomChannels[room.RoomId] = channel;
        _channelChoiceDirty = true;
    }

    private void RestoreChannel(ConversationVm conv, ChatRoom room)
    {
        if (conv.PendingChannel is not { } wanted) return;
        if (!conv.Channels.Any(c => c.IsText && c.Id == wanted)) return;
        conv.PendingChannel = null;
        conv.SelectChannel(wanted);
    }
}
