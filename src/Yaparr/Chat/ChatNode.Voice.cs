using Yaparr.Identity;
using Yaparr.Transport;
using Yaparr.Voice;

namespace Yaparr.Chat;

// Voice channels: who is in the channel is a small signed statement that spreads through the room like presence;
// the audio itself goes straight to each person in the channel as unreliable encrypted datagrams.
public sealed partial class ChatNode
{
    private TimeSpan VoiceLifetime => _options.VoiceInterval * 3;

    internal bool IsConnected(PeerId id) => _peers.TryGetValue(id, out var p) && p.Connection.State == ConnectionState.Established;

    internal VoiceSession JoinVoice(ChatRoom room)
    {
        room.ActiveVoice?.Dispose();
        var session = new VoiceSession(this, room);
        room.ActiveVoice = session;
        room.SelfMuted = room.SelfDeafened = false;
        PublishVoiceState(room, active: true, muted: false, deafened: false);
        session.RosterChanged(ActiveVoiceOthers(room));
        return session;
    }

    internal void EndVoice(ChatRoom room, VoiceSession session)
    {
        if (!ReferenceEquals(room.ActiveVoice, session)) return;
        room.ActiveVoice = null;
        LeaveVoiceGroups(room);
        PublishVoiceState(room, active: false, muted: false, deafened: false);
    }

    internal void PublishVoiceState(ChatRoom room, bool active, bool muted, bool deafened, bool raise = true)
    {
        room.SelfMuted = muted;
        room.SelfDeafened = deafened;
        var state = SignedVoice.Create(_identity, room.Key, DisplayName, NextTimestamp(), active, muted, deafened);
        room.LastVoiceAt = Environment.TickCount64;
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Voice, Room = RefTo(room, member), Vox = new[] { state } });
        if (raise) room.RaiseVoiceChanged();
    }

    private void SendVoiceStateTo(ChatRoom room, PeerState peer)
    {
        var states = new List<StoredVoice>();
        if (room.ActiveVoice != null)
            states.Add(SignedVoice.Create(_identity, room.Key, DisplayName, NextTimestamp(), true, room.SelfMuted, room.SelfDeafened));
        long cutoff = Environment.TickCount64 - (long)VoiceLifetime.TotalMilliseconds;
        lock (room.IndirectGate)
            states.AddRange(room.Voice.Values.Where(e => e.State.Active && e.SeenAt >= cutoff && e.State.From != peer.Id.Value).Take(15).Select(e => e.State));
        if (states.Count > 0)
            Send(peer.Connection, new Wire { Type = Wire.Voice, Room = RefTo(room, peer), Vox = states.ToArray() });
    }

    private void OnVoiceState(PeerState peer, ChatRoom room, StoredVoice[] states)
    {
        bool changed = false;
        foreach (var s in states.Take(16))
        {
            if (!IsValidPeerId(s.From) || s.From == Id.Value || !SignedVoice.Verify(s, room.Key)) continue;
            var id = new PeerId(s.From);
            lock (room.IndirectGate)
            {
                if (room.Voice.TryGetValue(id, out var old) && old.State.Ts >= s.Ts) continue;
                if (room.Voice.Count >= 200 && !room.Voice.ContainsKey(id)) continue;
                room.Voice[id] = new VoiceEntry(s, Environment.TickCount64);
            }
            changed = true;
            Relay(peer, room, s.From, reference => new Wire { Type = Wire.Voice, Room = reference, Vox = new[] { s } });
        }
        if (changed) VoiceRosterChanged(room);
    }

    private void VoiceRosterChanged(ChatRoom room)
    {
        room.RaiseVoiceChanged();
        room.ActiveVoice?.RosterChanged(ActiveVoiceOthers(room));
    }

    private void MaintainVoice(ChatRoom room, long now)
    {
        MaintainVoiceTree(room, now);
        if (room.ActiveVoice != null && now - room.LastVoiceAt >= _options.VoiceInterval.TotalMilliseconds)
            PublishVoiceState(room, active: true, room.SelfMuted, room.SelfDeafened, raise: false);

        long stale = now - (long)VoiceLifetime.TotalMilliseconds;
        bool removed;
        lock (room.IndirectGate)
        {
            var old = room.Voice.Where(kv => kv.Value.SeenAt < stale || !kv.Value.State.Active).Select(kv => kv.Key).ToList();
            foreach (var id in old) room.Voice.Remove(id);
            removed = old.Count > 0;
        }
        if (removed) VoiceRosterChanged(room);
    }

    private List<PeerId> ActiveVoiceOthers(ChatRoom room)
    {
        long cutoff = Environment.TickCount64 - (long)VoiceLifetime.TotalMilliseconds;
        lock (room.IndirectGate)
            return room.Voice.Where(kv => kv.Value.State.Active && kv.Value.SeenAt >= cutoff && !IsBlocked(kv.Key)).Select(kv => kv.Key).ToList();
    }

    internal IReadOnlyList<VoiceParticipant> GetVoiceParticipants(ChatRoom room)
    {
        var result = new List<VoiceParticipant>();
        if (room.ActiveVoice != null)
            result.Add(new VoiceParticipant(new PeerInfo(Id, DisplayName, null, AccountId: AccountId), room.SelfMuted, room.SelfDeafened, IsSelf: true));
        long cutoff = Environment.TickCount64 - (long)VoiceLifetime.TotalMilliseconds;
        lock (room.IndirectGate)
            foreach (var (id, entry) in room.Voice.Where(kv => kv.Value.State.Active && kv.Value.SeenAt >= cutoff && !IsBlocked(kv.Key)).OrderBy(kv => kv.Value.State.Name))
            {
                var info = _peers.TryGetValue(id, out var p) ? p.Info : new PeerInfo(id, entry.State.Name, null, IsRelayed: true, AccountId: AccountOf(id));
                result.Add(new VoiceParticipant(info with { DisplayName = entry.State.Name }, entry.State.Muted, entry.State.Deafened, IsSelf: false));
            }
        return result;
    }

    internal void SendVoicePacket(ChatRoom room, ReadOnlySpan<byte> packet)
    {
        // In a big channel the audio goes up the tree, which passes it on; until we are in it, straight to each person.
        if (AttachedVoiceGroup(room) is { } tree)
        {
            try { PublishMedia(tree, packet, isKey: false); return; }
            catch (ArgumentException) { return; }
        }
        foreach (var id in ActiveVoiceOthers(room))
        {
            if (!_peers.TryGetValue(id, out var peer) || peer.Connection.State != ConnectionState.Established) continue;
            try { peer.Connection.SendUnreliable(ChannelIds.Voice, packet); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        }
    }

    private void OnVoiceData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (reliable || conn.RemoteId is not { } from || IsBlocked(from)) return;
        foreach (var room in _rooms.Values)
        {
            var session = room.ActiveVoice;
            if (session == null) continue;
            bool known = room.MemberStates.ContainsKey(from);
            if (!known) lock (room.IndirectGate) known = room.Voice.ContainsKey(from);
            if (known) session.OnPacket(from, data.Span);
        }
    }
}
