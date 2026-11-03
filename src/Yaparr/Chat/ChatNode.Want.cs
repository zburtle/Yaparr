using Yaparr.Transport;

namespace Yaparr.Chat;

// Asking for particular messages that a room's history on this computer lacks, such as the message a reply answers. The history
// exchange when someone connects sends only the newest messages, so older ones are still held by members but not handed over.
public sealed partial class ChatNode
{
    /// <summary>The most messages asked for in one request.</summary>
    public const int MaxWanted = 20;

    private const long WantWindowMs = 10_000;

    /// <summary>Asks up to three connected members of <paramref name="room"/> for the messages with these keys.</summary>
    internal int RequestMessages(ChatRoom room, IEnumerable<string> keys)
    {
        var wanted = keys.Where(MessageEnvelope.IsKey).Select(k => k.ToLowerInvariant()).Distinct()
            .Where(k => !room.History.Keys().Contains(k)).Take(MaxWanted).ToArray();
        if (wanted.Length == 0 || !_options.SyncHistory) return 0;
        // Those we reach directly first: asking through a relay would make a member pay for a request that may find nothing.
        var asked = room.MemberStates.Values.Where(m => m.Connection.State == ConnectionState.Established)
            .OrderBy(m => m.Connection.IsRelayed).Take(3).ToList();
        foreach (var peer in asked)
        {
            peer.HistoryExpectedUntil = Math.Max(peer.HistoryExpectedUntil, Environment.TickCount64 + WantWindowMs);
            Send(peer.Connection, new Wire { Type = Wire.Want, Room = RefTo(room, peer), Have = wanted });
        }
        return asked.Count;
    }

    private void OnWantRequest(PeerState peer, ChatRoom room, string[] keys)
    {
        // Members are allowed a request every couple of seconds; more are ignored.
        if (!_options.SyncHistory || !peer.TryStartWanting($"{room.Key}:{room.Epoch}")) return;
        var found = room.History.Find(keys.Take(MaxWanted).Where(MessageEnvelope.IsKey).Select(k => k.ToLowerInvariant()));
        if (found.Count > 0) Send(peer.Connection, new Wire { Type = Wire.History, Room = RefTo(room, peer), Msgs = found.ToArray() });
    }
}
