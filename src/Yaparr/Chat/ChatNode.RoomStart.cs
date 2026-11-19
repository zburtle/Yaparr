using Yaparr.Identity;

namespace Yaparr.Chat;

// When a room began. The owner of a room made with this version writes one signed record when it is made (a message of its own, kept in a channel that is none of the
// room's, so it is never shown): everyone who has it knows the room began then, and so that reading back through the history has reached the beginning once nothing
// earlier is held. Without it (a room made before, or one with no owner) the end of the history can only be a guess: nothing earlier here, and nobody connected has more.
public sealed partial class ChatNode
{
    // The record is the owner's if the account it proves is the room's owner and the device it was written on belongs to that account.
    private bool IsOwnersStart(ChatRoom room, StoredMessage m)
    {
        if (m.Chan != RoomChannels.StartMarker || room.Owner is not { } owner || !MessageEnvelope.TryReadStart(m.Text, out var account, out var certificate)) return false;
        try
        {
            var pub = Convert.FromBase64String(m.Pub);
            if (account == null != (certificate == null)) return false;
            // The account's own key signed it, or a device with the account's certificate did.
            var accountKey = account == null ? pub : Convert.FromBase64String(account);
            if (account != null && !PeerIdentity.VerifyDevice(accountKey, pub, Convert.FromBase64String(certificate!))) return false;
            return PeerId.FromPublicKey(accountKey) == owner;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }
    }

    /// <summary>When the room began, if its owner's start record is held (anyone else's is ignored); null when it is not.</summary>
    internal DateTimeOffset? RoomStartOf(ChatRoom room)
    {
        if (room.Owner == null) return null;
        long ts = long.MaxValue;
        string key = "";
        for (int round = 0; round < 8; round++)
        {
            var page = room.History.Before(RoomChannels.StartMarker, ts, key, 16);
            if (page.Count == 0) return null;
            // The earliest of the owner's: a record that is not the owner's does not count.
            var mine = page.Where(m => IsOwnersStart(room, m)).OrderBy(m => m.Ts).FirstOrDefault();
            if (mine != null) return DateTimeOffset.FromUnixTimeMilliseconds(mine.Ts);
            if (page.Count < 16) return null;
            ts = page[^1].Ts;
            key = SignedMessage.Key(page[^1]);
        }
        return null;
    }

    /// <summary>The owner writes the room's start record, once, when the room is made. Nothing happens for a room you do not own or that already has one.</summary>
    internal void WriteRoomStart(ChatRoom room)
    {
        if (room.Owner != AccountId || RoomStartOf(room) != null) return;
        SendRoomMessage(room, MessageEnvelope.WrapStart(_identity), RoomChannels.StartMarker);
    }
}
