using Yaparr.Identity;

namespace Yaparr.Chat;

// A voice channel with many people cannot have a connection between every pair, so once it is big enough it uses a
// media tree (see ChatNode.Media.cs). One member who can relay becomes the root: the one who sorts first by id starts
// it, and the others wait a little longer for each place they sit further down the list, so only one does. Everybody
// joins that tree and sends their audio up it; each member passes on what it gets. If the root disappears, the next
// one in line starts a new tree. While there is no tree, voice goes straight to each person as before.
public sealed partial class ChatNode
{
    // Each voice channel has a tree of its own; the first one's is called "voice" as it always was, the others by their channel id.
    private static string VoiceTreeName(string channel) => channel == RoomChannels.DefaultVoice ? "voice" : channel;

    private static string ActiveChannel(ChatRoom room) => room.ActiveVoice?.ChannelId ?? RoomChannels.DefaultVoice;

    private MediaGroup? LiveVoiceGroup(ChatRoom room, long now) =>
        room.Media.Values
            .Where(g => g.Kind == "voice" && g.Info.Name == VoiceTreeName(ActiveChannel(room)) && !g.Suspect && g.Ann.Active && (g.IsRoot || now - g.AnnSeenAt <= LifetimeMs(g)))
            .OrderBy(g => g.Info.Root.Value, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>True while a voice tree exists for the room, so the audio should go through it and not to each person.</summary>
    internal bool VoiceTreeActive(ChatRoom room) => LiveVoiceGroup(room, Environment.TickCount64) != null;

    // The voice group we are attached to, if any.
    private MediaGroup? AttachedVoiceGroup(ChatRoom room) =>
        room.Media.Values.FirstOrDefault(g => g.Kind == "voice" && g.Info.Name == VoiceTreeName(ActiveChannel(room)) && g.Joined && g.Attached);

    // What we say of ourselves for choosing the root: 0 when we cannot relay, otherwise 1 plus our upload in megabits a second (1 until it is measured).
    private int RelayHint() =>
        !IsRelayCapable ? 0 : MeasuredUploadBytesPerSecond is { } up ? (int)Math.Min(255, 1 + up / 125_000) : 1;

    // The members that could start the tree, best first: the ones that say they cannot relay are left out, those that say nothing (older versions)
    // come after those that say how well they can, and among equals the lower id comes first. Everybody sees the same order up to what each has
    // heard so far, and if two start at once the lower root id stays (see MaintainVoiceTree).
    private List<(PeerId Id, int Up)> RootCandidates(ChatRoom room, List<PeerId> others, long now)
    {
        // The root of the tree that has just gone is left out: its announcement of being in the call lingers for a while, and waiting for it
        // would put the one who is next in line behind a member who is not there. Everyone has the same next in line, so it starts at once.
        PeerId? gone = room.VoiceTreeLastRoot is { } last && last != Id && now - room.VoiceTreeLostAt < VoiceLifetime.TotalMilliseconds ? last : null;
        if (gone != null) others = others.Where(id => id != gone).ToList();
        var list = new List<(PeerId, int)> { (Id, RelayHint()) };
        lock (room.IndirectGate)
            foreach (var id in others)
                if (room.Voice.TryGetValue(id, out var e) && e.State.Up != 0) list.Add((id, e.State.Up ?? 0));
        return list.Where(c => c.Item2 > 0 || c.Item1 != Id).OrderByDescending(c => c.Item2).ThenBy(c => c.Item1.Value, StringComparer.Ordinal).ToList();
    }

    private void MaintainVoiceTree(ChatRoom room, long now)
    {
        if (room.ActiveVoice == null)
        {
            foreach (var g in room.Media.Values.Where(g => g.Kind == "voice" && (g.Joined || g.IsRoot)).ToList()) LeaveVoiceGroup(g);
            room.VoiceTreeWantedSince = room.VoiceTreeLowSince = 0;
            room.VoiceTreeLastRoot = null;
            room.VoiceTreeLostAt = 0;
            return;
        }

        var others = ActiveVoiceOthers(room);
        int count = others.Count + 1;
        var live = LiveVoiceGroup(room, now);

        if (live != null)
        {
            room.VoiceTreeWantedSince = 0;
            room.VoiceTreeLastRoot = live.Info.Root;
            room.VoiceTreeLostAt = 0;
            // Two trees at once (two members started at the same moment): the one with the lower root id stays.
            foreach (var other in room.Media.Values.Where(g => g.Kind == "voice" && !ReferenceEquals(g, live) && (g.Joined || g.IsRoot)).ToList())
                LeaveVoiceGroup(other);

            if (live.IsRoot)
            {
                // Everyone left but a few: the tree is not needed, and the root ends it.
                if (count > _options.VoiceTreeThreshold / 2) room.VoiceTreeLowSince = 0;
                else if (room.VoiceTreeLowSince == 0) room.VoiceTreeLowSince = now;
                else if (now - room.VoiceTreeLowSince > LifetimeMs(live)) { room.VoiceTreeLowSince = 0; LeaveVoiceGroup(live); }
                return;
            }
            JoinVoiceGroup(room, live);
            return;
        }

        room.VoiceTreeLowSince = 0;
        if (room.VoiceTreeLastRoot != null && room.VoiceTreeLostAt == 0) room.VoiceTreeLostAt = now;
        if (count < _options.VoiceTreeThreshold || !IsRelayCapable) { room.VoiceTreeWantedSince = 0; return; }
        if (room.VoiceTreeWantedSince == 0) room.VoiceTreeWantedSince = now;
        // We wait for each member who suits better to have its turn first.
        var candidates = RootCandidates(room, others, now);
        int rank = Math.Max(0, candidates.FindIndex(c => c.Id == Id));
        if (now - room.VoiceTreeWantedSince < rank * _options.VoiceTreeRankDelay.TotalMilliseconds) return;

        _options.Log?.Invoke($"voice in '{room.Name}' has {count} people; starting a voice tree");
        var group = CreateRootGroup(room, "voice", VoiceTreeName(ActiveChannel(room)), signed: false, upstream: true, _options.VoiceRootFanout, kbps: 64);
        BindVoice(room, group);
        room.VoiceTreeWantedSince = 0;
    }

    private void BindVoice(ChatRoom room, MediaGroup g)
    {
        lock (g.Gate)
        {
            g.Joined = true;
            g.Deliver = packet => room.ActiveVoice?.OnPacket(packet.Origin, packet.Payload.Span);
        }
    }

    private void JoinVoiceGroup(ChatRoom room, MediaGroup g)
    {
        bool fresh;
        lock (g.Gate) fresh = !g.Joined;
        if (fresh) BindVoice(room, g);
        AdvanceMedia(g);
    }

    private void LeaveVoiceGroup(MediaGroup g)
    {
        if (g.IsRoot) StopMedia(g);
        else LeaveMedia(g);
    }

    private void LeaveVoiceGroups(ChatRoom room)
    {
        foreach (var g in room.Media.Values.Where(g => g.Kind == "voice" && (g.Joined || g.IsRoot)).ToList()) LeaveVoiceGroup(g);
    }
}
