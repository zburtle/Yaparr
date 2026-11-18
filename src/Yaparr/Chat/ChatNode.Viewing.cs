using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>What one device of an account has on show: a room's text channel, or a conversation with one person. Null for none (the window is not in front, or the person is away).</summary>
public sealed record ViewTarget(string? RoomId, string? Channel, PeerId? DirectWith);

// Your devices tell each other what they are showing, so that the phone does not make a notification for a message you are looking at on the computer. It goes only to
// the other devices of the same account (the connection's certificate says which), it is not saved, and it counts for a few minutes unless it is said again.
public sealed partial class ChatNode
{
    /// <summary>How long what a sibling said it is showing counts without being said again. A device that is showing something says it again every minute or so.</summary>
    public static readonly TimeSpan ViewingFresh = TimeSpan.FromMinutes(3);

    private ViewTarget? _viewing;
    private readonly object _viewGate = new();

    /// <summary>
    /// Says what this device is showing, to the other devices of the account (null for nothing). Call it when the person opens another chat or the window comes to the
    /// front or goes away, and again every minute while something is shown, so that a device that is switched off does not keep a chat looking open.
    /// </summary>
    public void SetViewing(ViewTarget? target)
    {
        lock (_viewGate) _viewing = target;
        foreach (var sibling in _peers.Values.Where(IsSibling)) SendViewing(sibling);
    }

    private void SendViewing(PeerState sibling)
    {
        ViewTarget? target;
        lock (_viewGate) target = _viewing;
        var wire = new Wire { Type = Wire.View, Room = "", Text = "", Id = "" };
        if (target?.RoomId is { } roomId && _rooms.TryGetValue(roomId, out var room))
        {
            wire.Room = RefTo(room, sibling);
            wire.Text = RoomChannels.Normalize(target.Channel);
        }
        else if (target?.DirectWith is { } person) wire.Id = AccountOf(person).Value;
        Send(sibling.Connection, wire);
    }

    private void OnViewing(PeerState peer, Wire wire)
    {
        // Only another of your own devices may say what it is showing.
        if (!IsSibling(peer)) return;
        ViewTarget? target = null;
        if (!string.IsNullOrEmpty(wire.Room) && ResolveRoom(peer, wire.Room) is { } room) target = new ViewTarget(room.Key, RoomChannels.Normalize(wire.Text), null);
        else if (!string.IsNullOrEmpty(wire.Id) && IsValidPeerId(wire.Id)) target = new ViewTarget(null, null, new PeerId(wire.Id));
        peer.Viewing = target;
        peer.ViewingAt = Environment.TickCount64;
    }

    /// <summary>Whether another of your devices that is connected is in use right now (it is showing some chat in front of the person, whichever it is).</summary>
    public bool SiblingIsActive()
    {
        long now = Environment.TickCount64;
        foreach (var peer in _peers.Values)
            if (IsSibling(peer) && peer.Viewing != null && now - peer.ViewingAt <= ViewingFresh.TotalMilliseconds) return true;
        return false;
    }

    /// <summary>
    /// Whether another of your devices that is connected is showing this chat right now: the room's text channel (<paramref name="channel"/> null is the first one), or
    /// the conversation with <paramref name="directWith"/>. A phone does not notify for what the computer in front of the person already shows.
    /// </summary>
    public bool SiblingIsViewing(string? roomId, string? channel, PeerId? directWith)
    {
        long now = Environment.TickCount64;
        var wanted = roomId == null ? null : RoomChannels.Normalize(channel);
        var person = directWith is { } d ? AccountOf(d) : (PeerId?)null;
        foreach (var peer in _peers.Values)
        {
            if (!IsSibling(peer) || peer.Viewing is not { } seen || now - peer.ViewingAt > ViewingFresh.TotalMilliseconds) continue;
            if (roomId != null ? seen.RoomId == roomId && seen.Channel == wanted : seen.DirectWith is { } other && person == AccountOf(other)) return true;
        }
        return false;
    }
}
