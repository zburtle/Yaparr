using Yaparr.Identity;
using Yaparr.Transport;
using Yaparr.Voice;

namespace Yaparr.Chat;

// Voice channels: who is in the channel is a small signed statement that spreads through the room like presence;
// the audio itself goes straight to each person in the channel as unreliable encrypted datagrams.
public sealed partial class ChatNode
{
    private TimeSpan VoiceLifetime => _options.VoiceInterval * 3;

    // A connection that has gone silent still counts as established until the idle timeout (35 seconds), long after the audio stopped. People send
    // at least a ping every ten seconds, so one we have heard nothing from for fourteen is not there.
    // Someone who pings every couple of seconds during a call (see MaintainVoice) is judged by a short silence, though not on one lost ping.
    private const long LiveWithinMs = 14_000, LiveWithinFastMs = 6_000;

    internal bool IsLive(PeerId id) =>
        _peers.TryGetValue(id, out var p) && p.Connection.State == ConnectionState.Established
        && Environment.TickCount64 - p.Connection.LastReceivedAt <= (p.Connection.SendsFastPings ? LiveWithinFastMs : LiveWithinMs);

    /// <summary>Whether the machine has a network connection at all, as the system reports it; false when Wi-Fi drops or the cable is pulled.</summary>
    public static bool NetworkAvailable => System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

    internal bool IsConnected(PeerId id) => _peers.TryGetValue(id, out var p) && p.Connection.State == ConnectionState.Established;

    internal VoiceSession JoinVoice(ChatRoom room, string channel)
    {
        channel = string.IsNullOrEmpty(channel) ? RoomChannels.DefaultVoice : channel;
        if (FindChannel(room, channel) is not { Kind: ChannelKind.Voice }) throw new InvalidOperationException("There is no such voice channel in this room.");
        // Moving to another voice channel leaves the one you are in.
        room.ActiveVoice?.Dispose();
        var session = new VoiceSession(this, room, channel);
        room.ActiveVoice = session;
        room.SelfMuted = room.SelfDeafened = false;
        PublishVoiceState(room, active: true, muted: false, deafened: false);
        TellDevicesYouJoinedVoice(room);
        session.RosterChanged(ActiveVoiceOthers(room));
        FetchMissingSounds(room);
        return session;
    }

    // You are one person in a voice channel, however many devices you have: the others hear of it and leave (see OnVoiceTakeover).
    private void TellDevicesYouJoinedVoice(ChatRoom room)
    {
        foreach (var sibling in _peers.Values.Where(IsSibling))
            if (sibling.Connection.SessionId != null) Send(sibling.Connection, new Wire { Type = Wire.Takeover, Room = RefTo(room, sibling) });
    }

    private void OnVoiceTakeover(PeerState sibling, ChatRoom room)
    {
        var session = room.ActiveVoice;
        if (session == null) return;
        // Both joined at the very same moment and each told the other: the one with the lower id stays.
        if (Environment.TickCount64 - session.JoinedAt < 1500 && string.CompareOrdinal(Id.Value, sibling.Id.Value) < 0) return;
        _options.Log?.Invoke($"you joined voice in '{room.Name}' on another of your devices ({sibling.Id.Short}); leaving it here");
        session.Leave();
        room.RaiseVoiceTakenOver(sibling.Id);
    }

    internal void EndVoice(ChatRoom room, VoiceSession session)
    {
        if (!ReferenceEquals(room.ActiveVoice, session)) return;
        room.ActiveVoice = null;
        LeaveVoiceGroups(room);
        PublishVoiceState(room, active: false, muted: false, deafened: false, channel: session.ChannelId);
    }

    internal void PublishVoiceState(ChatRoom room, bool active, bool muted, bool deafened, bool raise = true, string? channel = null)
    {
        room.SelfMuted = muted;
        room.SelfDeafened = deafened;
        var state = SignedVoice.Create(_identity, room.Key, DisplayName, NextTimestamp(), active, muted, deafened, channel ?? room.ActiveVoice?.ChannelId);
        room.LastVoiceAt = Environment.TickCount64;
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Voice, Room = RefTo(room, member), Vox = new[] { state } });
        if (raise) room.RaiseVoiceChanged();
    }

    private void SendVoiceStateTo(ChatRoom room, PeerState peer)
    {
        var states = new List<StoredVoice>();
        if (room.ActiveVoice != null)
            states.Add(SignedVoice.Create(_identity, room.Key, DisplayName, NextTimestamp(), true, room.SelfMuted, room.SelfDeafened, room.ActiveVoice.ChannelId));
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
            if (!IsValidPeerId(s.From) || s.From == Id.Value || IsBannedIn(room, new PeerId(s.From)) || !SignedVoice.Verify(s, room.Key)) continue;
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
        // In a call everyone pings the others every couple of seconds, so that a person who drops is noticed in a few seconds; a connection
        // otherwise pings once in ten, and a muted person who says nothing sends nothing else.
        if (room.ActiveVoice != null)
            foreach (var id in ActiveVoiceOthers(room))
                if (_peers.TryGetValue(id, out var voicePeer)) voicePeer.Connection.SendPing();
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

    // The people in the voice channel we are in, which is the only one whose audio we send to or play. Someone who is deafened hears nothing,
    // so sending to them is a waste; they are still part of the channel (we stay connected to them for when they listen again).
    private List<PeerId> ActiveVoiceOthers(ChatRoom room, bool includeDeafened = true)
    {
        long cutoff = Environment.TickCount64 - (long)VoiceLifetime.TotalMilliseconds;
        string channel = room.ActiveVoice?.ChannelId ?? RoomChannels.DefaultVoice;
        lock (room.IndirectGate)
            return room.Voice.Where(kv => kv.Value.State.Active && kv.Value.SeenAt >= cutoff && !IsBlocked(kv.Key)
                                          && (includeDeafened || !kv.Value.State.Deafened)
                                          && (kv.Value.State.Chan ?? RoomChannels.DefaultVoice) == channel).Select(kv => kv.Key).ToList();
    }

    /// <summary>
    /// Who is in the voice channel you are in and whom you cannot reach right now. In a big call the audio goes through a tree, so being
    /// attached to it reaches everyone; otherwise it is a connection to each person.
    /// </summary>
    internal VoiceReach GetVoiceReach(ChatRoom room)
    {
        if (room.ActiveVoice == null) return new VoiceReach(0, Array.Empty<PeerId>());
        var others = ActiveVoiceOthers(room);
        if (AttachedVoiceGroup(room) != null) return new VoiceReach(others.Count, Array.Empty<PeerId>());
        return new VoiceReach(others.Count, others.Where(id => !IsLive(id)).ToList());
    }

    /// <summary>
    /// The voice channel another device of this account is in, as far as this device has heard (they announce it to the room like anyone
    /// in a call), or null when none is. This device's own call does not count.
    /// </summary>
    internal string? ChannelOfMyOtherDevice(ChatRoom room)
    {
        long cutoff = Environment.TickCount64 - (long)VoiceLifetime.TotalMilliseconds;
        lock (room.IndirectGate)
            return room.Voice.Where(kv => kv.Value.State.Active && kv.Value.SeenAt >= cutoff && kv.Key != Id && IsMyDevice(kv.Key))
                .OrderBy(kv => kv.Key.Value, StringComparer.Ordinal)
                .Select(kv => kv.Value.State.Chan ?? RoomChannels.DefaultVoice).FirstOrDefault();
    }

    internal IReadOnlyList<VoiceParticipant> GetVoiceParticipants(ChatRoom room)
    {
        var result = new List<VoiceParticipant>();
        if (room.ActiveVoice != null)
            result.Add(new VoiceParticipant(new PeerInfo(Id, DisplayName, null, AccountId: AccountId), room.SelfMuted, room.SelfDeafened, IsSelf: true, room.ActiveVoice.ChannelId));
        long cutoff = Environment.TickCount64 - (long)VoiceLifetime.TotalMilliseconds;
        lock (room.IndirectGate)
            foreach (var (id, entry) in room.Voice.Where(kv => kv.Value.State.Active && kv.Value.SeenAt >= cutoff && !IsBlocked(kv.Key)).OrderBy(kv => kv.Value.State.Name))
            {
                var info = _peers.TryGetValue(id, out var p) ? p.Info : new PeerInfo(id, entry.State.Name, null, IsRelayed: true, AccountId: AccountOf(id));
                result.Add(new VoiceParticipant(info with { DisplayName = entry.State.Name }, entry.State.Muted, entry.State.Deafened, IsSelf: false, entry.State.Chan ?? RoomChannels.DefaultVoice));
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
        foreach (var id in ActiveVoiceOthers(room, includeDeafened: false))
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
