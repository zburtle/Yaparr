namespace Yaparr.Chat;

// A voice channel with many people cannot have a connection between every pair, so once it is big enough it uses a
// media tree (see ChatNode.Media.cs). One member who can relay becomes the root: the one who sorts first by id starts
// it, and the others wait a little longer for each place they sit further down the list, so only one does. Everybody
// joins that tree and sends their audio up it; each member passes on what it gets. If the root disappears, the next
// one in line starts a new tree. While there is no tree, voice goes straight to each person as before.
public sealed partial class ChatNode
{
    private MediaGroup? LiveVoiceGroup(ChatRoom room, long now) =>
        room.Media.Values
            .Where(g => g.Kind == "voice" && !g.Suspect && g.Ann.Active && (g.IsRoot || now - g.AnnSeenAt <= MediaLifetimeMs))
            .OrderBy(g => g.Info.Root.Value, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>True while a voice tree exists for the room, so the audio should go through it and not to each person.</summary>
    internal bool VoiceTreeActive(ChatRoom room) => LiveVoiceGroup(room, Environment.TickCount64) != null;

    // The voice group we are attached to, if any.
    private MediaGroup? AttachedVoiceGroup(ChatRoom room) =>
        room.Media.Values.FirstOrDefault(g => g.Kind == "voice" && g.Joined && g.Attached);

    private void MaintainVoiceTree(ChatRoom room, long now)
    {
        if (room.ActiveVoice == null)
        {
            foreach (var g in room.Media.Values.Where(g => g.Kind == "voice" && (g.Joined || g.IsRoot)).ToList()) LeaveVoiceGroup(g);
            room.VoiceTreeWantedSince = room.VoiceTreeLowSince = 0;
            return;
        }

        var others = ActiveVoiceOthers(room);
        int count = others.Count + 1;
        var live = LiveVoiceGroup(room, now);

        if (live != null)
        {
            room.VoiceTreeWantedSince = 0;
            // Two trees at once (two members started at the same moment): the one with the lower root id stays.
            foreach (var other in room.Media.Values.Where(g => g.Kind == "voice" && !ReferenceEquals(g, live) && (g.Joined || g.IsRoot)).ToList())
                LeaveVoiceGroup(other);

            if (live.IsRoot)
            {
                // Everyone left but a few: the tree is not needed, and the root ends it.
                if (count > _options.VoiceTreeThreshold / 2) room.VoiceTreeLowSince = 0;
                else if (room.VoiceTreeLowSince == 0) room.VoiceTreeLowSince = now;
                else if (now - room.VoiceTreeLowSince > MediaLifetimeMs) { room.VoiceTreeLowSince = 0; LeaveVoiceGroup(live); }
                return;
            }
            JoinVoiceGroup(room, live);
            return;
        }

        room.VoiceTreeLowSince = 0;
        if (count < _options.VoiceTreeThreshold || !IsRelayCapable) { room.VoiceTreeWantedSince = 0; return; }
        if (room.VoiceTreeWantedSince == 0) room.VoiceTreeWantedSince = now;
        int rank = others.Count(id => string.CompareOrdinal(id.Value, Id.Value) < 0);
        if (now - room.VoiceTreeWantedSince < rank * _options.VoiceTreeRankDelay.TotalMilliseconds) return;

        _options.Log?.Invoke($"voice in '{room.Name}' has {count} people; starting a voice tree");
        var group = CreateRootGroup(room, "voice", "voice", signed: false, upstream: true, _options.VoiceRootFanout);
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
