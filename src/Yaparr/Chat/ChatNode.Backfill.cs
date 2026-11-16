using Yaparr.Transport;

namespace Yaparr.Chat;

// Everything said in a room is kept, but a device only has what it has been given: one that joined late, or was restored from a backup that left old pictures
// out, lacks some of it. When someone scrolls back past the oldest message a device holds, the device asks the members that are connected for the messages before it
// (and, as they are shown, fetches the pictures they carry from whoever has them). Members answer from everything they hold.
public sealed partial class ChatNode
{
    /// <summary>The most earlier messages asked for, and served, at a time.</summary>
    public const int BackfillMax = 100;

    private const long BackfillWindowMs = 15_000;

    internal int RequestEarlierHistory(ChatRoom room, string? channel, string beforeKey)
    {
        if (!_options.SyncHistory || !MessageEnvelope.IsKey(beforeKey)) return 0;
        var at = room.History.Find(new[] { beforeKey.ToLowerInvariant() }).FirstOrDefault();
        if (at == null) return 0;
        channel = RoomChannels.Normalize(channel);
        // Those we reach directly first: asking through a relay would make a member pay for a request that may find nothing.
        var asked = room.MemberStates.Values.Where(m => m.Connection.State == ConnectionState.Established && !IsBlocked(m.Id))
            .OrderBy(m => m.Connection.IsRelayed).Take(3).ToList();
        if (asked.Count == 0) return 0;
        long now = Environment.TickCount64;
        room.BackfillBefore = at.Ts;
        room.BackfillUntil = now + BackfillWindowMs;
        foreach (var peer in asked)
        {
            peer.HistoryExpectedUntil = Math.Max(peer.HistoryExpectedUntil, now + BackfillWindowMs);
            Send(peer.Connection, new Wire
            {
                Type = Wire.Back, Room = RefTo(room, peer), Text = channel == RoomChannels.DefaultText ? "" : channel,
                Ts = at.Ts, Id = beforeKey.ToLowerInvariant(), N = BackfillMax,
            });
        }
        return asked.Count;
    }

    private void OnBackRequest(PeerState peer, ChatRoom room, string channel, long ts, string beforeKey, int count)
    {
        // A member may ask every couple of seconds; more are ignored.
        if (!_options.SyncHistory || !MessageEnvelope.IsKey(beforeKey) || !peer.TryStartWanting($"back:{room.Key}:{room.Epoch}")) return;
        if (channel.Length > 0 && channel.Length != 16) return;
        var page = room.History.Before(channel.Length == 0 ? null : channel, ts, beforeKey.ToLowerInvariant(), Math.Clamp(count, 1, BackfillMax));
        if (page.Count == 0) return;
        // The reactions on those messages go with them, so they are not asked for one by one.
        var reactions = room.History.ReactionsTo(page.Select(SignedMessage.Key).ToList());
        var all = page.Concat(reactions).OrderBy(m => m.Ts).ToList();
        for (int i = 0; i < all.Count; i += 20)
            Send(peer.Connection, new Wire { Type = Wire.History, Room = RefTo(room, peer), Msgs = all.Skip(i).Take(20).ToArray() });
    }
}
