using System.Collections.Concurrent;
using System.Security.Cryptography;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

internal sealed class ChildLink
{
    public long HeardAt;
    public int Cap;
}

internal sealed record MapNode(PeerId Parent, int Cap);

/// <summary>One shared stream as this node sees it: what it is, where we sit in its tree, and who we pass it to.</summary>
internal sealed class MediaGroup
{
    public readonly object Gate = new();
    public string Id = "";
    public uint Tag;
    public ChatRoom Room = null!;
    public StoredMedia Ann = null!;
    public MediaGroupInfo Info = null!;
    public byte[] RootPub = Array.Empty<byte>();
    public int RootFanout;
    public long AnnSeenAt, LastAnnouncedAt;
    /// <summary>We could not reach the root for a while, so for choosing a new one this group is treated as gone.</summary>
    public bool Suspect;

    /// <summary>We take part: packets are handed to <see cref="Deliver"/>, and we send too if the group is two-way.</summary>
    public bool Joined;
    public bool IsRoot;
    public PeerId? Parent;
    public long ParentHeardAt;
    public PeerId? Placed;
    public long PlacedAt, LastRequestAt, AttachDialSince, UnreachableSince;
    public readonly HashSet<PeerId> Exclude = new();
    public long ExcludeSince;
    public readonly Dictionary<PeerId, ChildLink> Children = new();
    /// <summary>Root only: who sits under whom, learned from the reports that travel up the tree.</summary>
    public readonly Dictionary<PeerId, MapNode> Map = new();
    public Action<MediaPacket>? Deliver;

    public uint Seq;
    public readonly Dictionary<ulong, SeqWindow> Windows = new();
    public readonly Dictionary<ulong, PeerId> Origins = new();
    public readonly Queue<byte[]> Cache = new();
    public int CacheBytes;
    public long Forwarded, Delivered, Dropped, Duplicates;

    public string Kind => Info.Kind;
    public bool Attached => IsRoot || Parent != null;
}

/// <summary>The sending end of a stream you share with a room. Dispose it to stop sharing.</summary>
public sealed class MediaSource : IDisposable
{
    private readonly ChatNode _node;
    private readonly MediaGroup _group;

    internal MediaSource(ChatNode node, MediaGroup group) { _node = node; _group = group; }

    public MediaGroupInfo Info => _group.Info;

    /// <summary>The most one packet can carry.</summary>
    public int MaxPayload => MediaWire.MaxPayload(_group.Info.Signed);

    /// <summary>Sends one packet to everyone watching. Mark the first packet of each independently decodable section as a key.</summary>
    public void Send(ReadOnlySpan<byte> payload, bool isKey = false) => _node.PublishMedia(_group, payload, isKey);

    public MediaStatus Status => _node.StatusOf(_group);

    public void Dispose() => _node.StopMedia(_group);
}

/// <summary>Watching a stream someone shares with the room. Dispose it to stop.</summary>
public sealed class MediaView : IDisposable
{
    private readonly ChatNode _node;
    private readonly MediaGroup _group;

    internal MediaView(ChatNode node, MediaGroup group)
    {
        _node = node;
        _group = group;
        lock (group.Gate) { group.Joined = true; group.Deliver = p => Received?.Invoke(p); }
    }

    public MediaGroupInfo Info => _group.Info;

    /// <summary>A packet arrived. It can come from any thread.</summary>
    public event Action<MediaPacket>? Received;

    public MediaStatus Status => _node.StatusOf(_group);

    public void Dispose() => _node.LeaveMedia(_group);
}

// Streams that many people take part in, such as a shared screen or a large voice channel, cannot be sent to each
// person from one machine. They are passed along a tree instead: the sharer (the root) sends to a few members, and
// each of those passes it on to a few more. The root decides where each newcomer attaches, which keeps the tree
// loop-free and shallow, and members who lose their parent just ask the root again.
public sealed partial class ChatNode
{
    private const int MaxMediaGroupsPerRoom = 16;
    private const long PlaceValidMs = 10_000, RequestGapMs = 300, ExcludeMs = 30_000, DialGiveUpMs = 8_000;
    private const int MaxCacheBytes = 512 * 1024, MediaGraceConnections = 24;
    private const int MediaGraceMs = 8_000;

    private bool TakesPartInStream => _rooms.Values.Any(r => r.Media.Values.Any(g => g.Joined || g.IsRoot));

    private readonly ConcurrentDictionary<uint, MediaGroup> _mediaByTag = new();
    private readonly ConcurrentDictionary<PeerId, long> _mediaDials = new();
    private TokenBucket? _mediaBudget;

    private TokenBucket MediaBudget =>
        LazyInitializer.EnsureInitialized(ref _mediaBudget, () => new TokenBucket(_options.MediaBytesPerSecond, Environment.TickCount64))!;

    // How long the root can stay out of reach, for a member that wants in, before the group is treated as gone.
    private long UnreachableMs => (long)(_options.MediaEdgeTimeout.TotalMilliseconds * 1.5);

    private long MediaLifetimeMs => (long)(_options.MediaAnnounceInterval.TotalMilliseconds * 3);

    // ---- public API -------------------------------------------------------------------------

    /// <summary>
    /// Starts sharing a stream with a room and returns the end you send into. Everyone in the room is told, and those
    /// who choose to watch (<see cref="WatchMedia"/>) receive what you send, through the tree.
    /// </summary>
    /// <param name="signed">You sign every packet, so members passing it on cannot alter it. Only you send.</param>
    /// <param name="upstream">Everyone in the group can send, not only you.</param>
    public MediaSource StartMedia(ChatRoom room, string kind, string name, bool signed = true, bool upstream = false, int? fanout = null)
    {
        if (!_rooms.ContainsKey(room.Key)) throw new InvalidOperationException("You are not in that room.");
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 16 || kind == "voice") throw new ArgumentException("Choose another kind of stream.", nameof(kind));
        return new MediaSource(this, CreateRootGroup(room, kind, name, signed, upstream, fanout ?? _options.MediaRootFanout));
    }

    /// <summary>Starts receiving a stream someone shared (see <see cref="ChatRoom.MediaGroups"/>).</summary>
    public MediaView WatchMedia(ChatRoom room, string groupId)
    {
        if (!room.Media.TryGetValue(groupId, out var g) || g.Kind == "voice") throw new InvalidOperationException("That stream is not shared any more.");
        if (g.IsRoot) throw new InvalidOperationException("You are sharing that stream yourself.");
        var view = new MediaView(this, g);
        AdvanceMedia(g);
        return view;
    }

    public MediaStatus? GetMediaStatus(ChatRoom room, string groupId) => room.Media.TryGetValue(groupId, out var g) ? StatusOf(g) : null;

    internal MediaStatus StatusOf(MediaGroup g)
    {
        lock (g.Gate)
            return new MediaStatus(g.Id, g.IsRoot, g.Parent, g.Children.Keys.ToList(), g.Delivered, g.Forwarded, g.Dropped, g.Duplicates);
    }

    // ---- starting, announcing, ending -------------------------------------------------------

    private MediaGroup CreateRootGroup(ChatRoom room, string kind, string name, bool signed, bool upstream, int fanout)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var ann = SignedMedia.Create(_identity, room.Key, kind, id, name, NextTimestamp(), active: true, Math.Clamp(fanout, 1, 64), signed, upstream);
        var g = AddMediaGroup(room, ann) ?? throw new InvalidOperationException("Could not start the stream; try again.");
        lock (g.Gate) { g.IsRoot = true; g.Joined = upstream; }
        SendMediaAnnounce(room, g, active: true);
        room.RaiseMediaChanged();
        return g;
    }

    private MediaGroup? AddMediaGroup(ChatRoom room, StoredMedia ann)
    {
        if (room.Media.Count >= MaxMediaGroupsPerRoom && !room.Media.ContainsKey(ann.Group)) return null;
        var g = new MediaGroup
        {
            Id = ann.Group,
            Tag = MediaWire.TagOf(ann.Group),
            Room = room,
            Ann = ann,
            RootPub = Convert.FromBase64String(ann.Pub),
            RootFanout = ann.Fanout,
            Info = new MediaGroupInfo(ann.Group, ann.Kind, ann.Name, new PeerId(ann.From), ann.Signed, ann.Upstream),
            AnnSeenAt = Environment.TickCount64,
        };
        if (!_mediaByTag.TryAdd(g.Tag, g)) return null;
        if (!room.Media.TryAdd(g.Id, g)) { _mediaByTag.TryRemove(g.Tag, out _); return null; }
        return g;
    }

    private void SendMediaAnnounce(ChatRoom room, MediaGroup g, bool active)
    {
        var ann = SignedMedia.Create(_identity, room.Key, g.Info.Kind, g.Id, g.Info.Name, NextTimestamp(), active, g.RootFanout, g.Info.Signed, g.Info.Upstream);
        lock (g.Gate) { g.Ann = ann; g.LastAnnouncedAt = g.AnnSeenAt = Environment.TickCount64; }
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Media, Room = RefTo(room, member), Ann = new[] { ann } });
    }

    private void SendMediaAnnouncesTo(ChatRoom room, PeerState peer)
    {
        var anns = room.Media.Values.Where(g => g.Ann.Active && (g.IsRoot || Environment.TickCount64 - g.AnnSeenAt <= MediaLifetimeMs)).Select(g => g.Ann).Take(8).ToArray();
        if (anns.Length > 0) Send(peer.Connection, new Wire { Type = Wire.Media, Room = RefTo(room, peer), Ann = anns });
    }

    private void OnMediaAnnounce(PeerState peer, ChatRoom room, StoredMedia[] anns)
    {
        bool changed = false;
        foreach (var a in anns.Take(8))
        {
            if (!IsValidPeerId(a.From) || a.From == Id.Value || !SignedMedia.Verify(a, room.Key) || IsBlocked(new PeerId(a.From))) continue;
            if (room.Media.TryGetValue(a.Group, out var g))
            {
                if (g.Ann.From != a.From || g.Ann.Ts >= a.Ts) continue;
                if (!a.Active)
                {
                    RemoveMediaGroup(g);
                    changed = true;
                    Relay(peer, room, a.From, reference => new Wire { Type = Wire.Media, Room = reference, Ann = new[] { a } });
                    continue;
                }
                lock (g.Gate) { g.Ann = a; g.AnnSeenAt = Environment.TickCount64; g.Suspect = false; }
            }
            else
            {
                if (!a.Active) continue;
                g = AddMediaGroup(room, a);
                if (g == null) continue;
                changed = true;
            }
            Relay(peer, room, a.From, reference => new Wire { Type = Wire.Media, Room = reference, Ann = new[] { a } });
            if (g.Joined) AdvanceMedia(g);
        }
        if (changed) room.RaiseMediaChanged();
    }

    internal void StopMedia(MediaGroup g)
    {
        if (!g.IsRoot) return;
        SendMediaAnnounce(g.Room, g, active: false);
        RemoveMediaGroup(g);
        g.Room.RaiseMediaChanged();
    }

    internal void LeaveMedia(MediaGroup g)
    {
        List<PeerId> neighbours;
        lock (g.Gate)
        {
            if (g.IsRoot) return;
            neighbours = Neighbours(g);
            g.Joined = false;
            g.Deliver = null;
            g.Parent = null;
            g.Placed = null;
            g.Children.Clear();
        }
        foreach (var id in neighbours) SendMedia(id, g, new WireMedia { Op = "leave" });
    }

    // Drops everything we hold about the group, telling the neighbours so they find another way.
    private void RemoveMediaGroup(MediaGroup g)
    {
        List<PeerId> neighbours;
        lock (g.Gate)
        {
            neighbours = Neighbours(g);
            g.Joined = false;
            g.Deliver = null;
            g.Parent = null;
            g.Children.Clear();
            g.Map.Clear();
        }
        foreach (var id in neighbours) SendMedia(id, g, new WireMedia { Op = "leave" });
        _mediaByTag.TryRemove(g.Tag, out _);
        g.Room.Media.TryRemove(g.Id, out _);
    }

    private List<PeerId> Neighbours(MediaGroup g)
    {
        var all = g.Children.Keys.ToList();
        if (g.Parent is { } p) all.Add(p);
        return all;
    }

    // ---- joining the tree -------------------------------------------------------------------

    private bool TryPeer(PeerId id, out PeerState state)
    {
        if (_peers.TryGetValue(id, out var s) && s.Connection.State == ConnectionState.Established) { state = s; return true; }
        state = null!;
        return false;
    }

    private void SendMedia(PeerId to, MediaGroup g, WireMedia body)
    {
        if (!TryPeer(to, out var state)) return;
        body.Group = g.Id;
        Send(state.Connection, new Wire { Type = Wire.Media, Room = RefTo(g.Room, state), Mx = body });
    }

    // Connects to a member we need as a neighbour, if we are not yet. Dialling can take a while, so this only starts it.
    private void EnsureConnected(PeerId id)
    {
        if (IsConnected(id) || IsBlocked(id) || !_mediaDials.TryAdd(id, Environment.TickCount64)) return;
        _ = Task.Run(async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(20_000);
            try { await OpenDirectChatAsync(id, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { _mediaDials.TryRemove(id, out _); }
        });
    }

    private int FanoutOf(MediaGroup g) => g.IsRoot ? g.RootFanout : IsRelayCapable ? _options.MediaFanout : 0;

    // Gets us attached if we want to be: asks the root where to go, or goes where it said.
    private void AdvanceMedia(MediaGroup g)
    {
        if (g.IsRoot) return;
        long now = Environment.TickCount64;
        PeerId? placed;
        List<string> exclude;
        lock (g.Gate)
        {
            if (!g.Joined || g.Parent != null) return;
            if (g.Exclude.Count > 0 && now - g.ExcludeSince > ExcludeMs) g.Exclude.Clear();
            if (now - g.LastRequestAt < RequestGapMs) return;
            g.LastRequestAt = now;
            if (g.Placed != null && now - g.PlacedAt > PlaceValidMs) g.Placed = null;
            placed = g.Placed;
            exclude = g.Exclude.Select(x => x.Value).ToList();
        }

        if (placed is { } parent)
        {
            if (!TryPeer(parent, out _))
            {
                EnsureConnected(parent);
                lock (g.Gate)
                {
                    if (g.AttachDialSince == 0) g.AttachDialSince = now;
                    else if (now - g.AttachDialSince > DialGiveUpMs) { g.Exclude.Add(parent); g.ExcludeSince = now; g.Placed = null; g.AttachDialSince = 0; }
                }
                return;
            }
            lock (g.Gate) g.AttachDialSince = 0;
            SendMedia(parent, g, new WireMedia { Op = "attach", Cap = FanoutOf(g) });
            return;
        }

        if (!TryPeer(g.Info.Root, out _))
        {
            EnsureConnected(g.Info.Root);
            lock (g.Gate)
            {
                if (g.UnreachableSince == 0) g.UnreachableSince = now;
                else if (now - g.UnreachableSince > UnreachableMs) g.Suspect = true;
            }
            return;
        }
        lock (g.Gate) g.UnreachableSince = 0;
        SendMedia(g.Info.Root, g, new WireMedia { Op = "join", Cap = FanoutOf(g), Exclude = exclude.ToArray() });
    }

    private void OnMediaControl(PeerState peer, ChatRoom room, WireMedia m)
    {
        if (!room.Media.TryGetValue(m.Group, out var g) || IsBlocked(peer.Id)) return;
        switch (m.Op)
        {
            case "join": OnMediaJoin(peer, g, m); break;
            case "place": OnMediaPlace(peer, g, m); break;
            case "attach": OnMediaAttach(peer, g, m); break;
            case "ok": OnMediaOk(peer, g); break;
            case "refuse": OnMediaRefuse(peer, g); break;
            case "leave": OnMediaLeave(peer, g); break;
            case "ping": OnMediaPing(peer, g); break;
            case "link": OnMediaLink(peer, g, m); break;
        }
    }

    // At the root: someone wants in. Say who they should attach to.
    private void OnMediaJoin(PeerState peer, MediaGroup g, WireMedia m)
    {
        if (!g.IsRoot) return;
        var exclude = (m.Exclude ?? Array.Empty<string>()).Take(32).Where(IsValidPeerId).Select(x => new PeerId(x)).ToHashSet();
        PeerId? place;
        PeerId? evicted = null;
        lock (g.Gate)
        {
            place = ChoosePlace(g, peer.Id, exclude);
            // No room, and the one asking can pass the stream on: if the sharer's own places are held by members who
            // cannot, one of them steps aside. It asks again and is placed under someone who can carry it.
            if (place == null && m.Cap > 0 && !g.Children.ContainsKey(peer.Id))
            {
                evicted = g.Children.Where(kv => kv.Value.Cap == 0).Select(kv => (PeerId?)kv.Key).FirstOrDefault();
                if (evicted is { } gone)
                {
                    g.Children.Remove(gone);
                    g.Map.Remove(gone);
                    place = g.Info.Root;
                }
            }
        }
        if (evicted is { } leaving) SendMedia(leaving, g, new WireMedia { Op = "leave" });
        SendMedia(peer.Id, g, new WireMedia { Op = "place", Parent = place?.Value ?? "" });
    }

    // The shallowest member with a free place, or the root itself. Never someone under the joiner, which would make a loop.
    private PeerId? ChoosePlace(MediaGroup g, PeerId joiner, HashSet<PeerId> exclude)
    {
        var barred = new HashSet<PeerId> { joiner };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var (id, node) in g.Map)
                if (!barred.Contains(id) && barred.Contains(node.Parent)) { barred.Add(id); grew = true; }
        }

        int DepthOf(PeerId id)
        {
            int depth = 0;
            for (var cur = id; cur != g.Info.Root; depth++)
            {
                if (depth > 32 || !g.Map.TryGetValue(cur, out var n)) return -1;
                cur = n.Parent;
            }
            return depth;
        }

        PeerId? best = null;
        (int Depth, int Free, string Id) bestKey = default;
        void Consider(PeerId id, int depth, int cap)
        {
            if (barred.Contains(id) || exclude.Contains(id) || depth < 0) return;
            int free = cap - g.Map.Values.Count(n => n.Parent == id);
            if (free <= 0) return;
            var key = (depth, -free, id.Value);
            if (best == null || key.CompareTo(bestKey) < 0) { best = id; bestKey = key; }
        }

        Consider(g.Info.Root, 0, g.RootFanout);
        foreach (var (id, node) in g.Map) Consider(id, DepthOf(id), node.Cap);
        return best;
    }

    // We asked the root where to attach.
    private void OnMediaPlace(PeerState peer, MediaGroup g, WireMedia m)
    {
        if (peer.Id != g.Info.Root || g.IsRoot) return;
        PeerId? parent = IsValidPeerId(m.Parent ?? "") ? new PeerId(m.Parent!) : null;
        long now = Environment.TickCount64;
        lock (g.Gate)
        {
            if (!g.Joined || g.Parent != null) return;
            if (parent == null) { g.Placed = null; g.LastRequestAt = now + 1500; return; }   // no room anywhere yet: try again shortly
            g.Placed = parent;
            g.PlacedAt = now;
            g.LastRequestAt = 0;
        }
        AdvanceMedia(g);
    }

    // Someone wants to attach under us.
    private void OnMediaAttach(PeerState peer, MediaGroup g, WireMedia m)
    {
        int cap = Math.Clamp(m.Cap, 0, 64);
        bool accept;
        lock (g.Gate)
        {
            bool already = g.Children.ContainsKey(peer.Id);
            accept = (g.IsRoot || g.Parent != null) && peer.Id != g.Parent && (already || g.Children.Count < FanoutOf(g));
            if (accept) g.Children[peer.Id] = new ChildLink { HeardAt = Environment.TickCount64, Cap = cap };
        }
        SendMedia(peer.Id, g, new WireMedia { Op = accept ? "ok" : "refuse" });
        if (!accept) return;
        _pinned[peer.Id.Value] = 0;
        ReportLink(g, peer.Id, add: true, cap);
        ReplayCache(g, peer);
    }

    private void OnMediaOk(PeerState peer, MediaGroup g)
    {
        bool wanted;
        lock (g.Gate)
        {
            wanted = g.Joined && !g.IsRoot && g.Parent == null && g.Placed == peer.Id;
            if (wanted)
            {
                g.Parent = peer.Id;
                g.ParentHeardAt = Environment.TickCount64;
                g.Placed = null;
                g.AttachDialSince = g.UnreachableSince = 0;
                g.Exclude.Clear();
                g.Suspect = false;
            }
        }
        if (!wanted) { SendMedia(peer.Id, g, new WireMedia { Op = "leave" }); return; }
        _pinned[peer.Id.Value] = 0;
        SendMedia(peer.Id, g, new WireMedia { Op = "ping" });
    }

    private void OnMediaRefuse(PeerState peer, MediaGroup g)
    {
        lock (g.Gate)
        {
            if (g.Placed != peer.Id) return;
            g.Exclude.Add(peer.Id);
            g.ExcludeSince = Environment.TickCount64;
            g.Placed = null;
            g.LastRequestAt = 0;
        }
        AdvanceMedia(g);
    }

    private void OnMediaLeave(PeerState peer, MediaGroup g)
    {
        bool wasChild, wasParent = false;
        lock (g.Gate)
        {
            wasChild = g.Children.Remove(peer.Id);
            if (g.Parent == peer.Id) { g.Parent = null; g.LastRequestAt = 0; wasParent = true; }
        }
        if (wasChild) ReportLink(g, peer.Id, add: false, 0);
        if (wasParent) AdvanceMedia(g);
    }

    private void OnMediaPing(PeerState peer, MediaGroup g)
    {
        bool known;
        lock (g.Gate)
        {
            long now = Environment.TickCount64;
            known = g.Parent == peer.Id || g.Children.ContainsKey(peer.Id);
            if (g.Parent == peer.Id) g.ParentHeardAt = now;
            else if (g.Children.TryGetValue(peer.Id, out var child)) child.HeardAt = now;
        }
        // They think we are neighbours and we do not: tell them, so they stop and find another way.
        if (!known) SendMedia(peer.Id, g, new WireMedia { Op = "leave" });
    }

    // ---- keeping the root's picture of the tree up to date ----------------------------------

    private void ReportLink(MediaGroup g, PeerId child, bool add, int cap)
    {
        var body = new WireMedia { Op = "link", Parent = Id.Value, Child = child.Value, Cap = cap, Add = add };
        ApplyOrForwardLink(g, body);
    }

    private void OnMediaLink(PeerState peer, MediaGroup g, WireMedia m)
    {
        lock (g.Gate) if (!g.Children.ContainsKey(peer.Id)) return;
        if (!IsValidPeerId(m.Parent ?? "") || !IsValidPeerId(m.Child ?? "")) return;
        ApplyOrForwardLink(g, m);
    }

    private void ApplyOrForwardLink(MediaGroup g, WireMedia body)
    {
        PeerId parent = new(body.Parent!), child = new(body.Child!);
        PeerId? up;
        lock (g.Gate)
        {
            up = g.Parent;
            if (g.IsRoot)
            {
                if (body.Add) g.Map[child] = new MapNode(parent, Math.Clamp(body.Cap, 0, 64));
                // A late removal must not undo a newer placement.
                else if (g.Map.TryGetValue(child, out var node) && node.Parent == parent) g.Map.Remove(child);
                return;
            }
        }
        if (up is { } next) SendMedia(next, g, new WireMedia { Op = "link", Parent = body.Parent, Child = body.Child, Cap = body.Cap, Add = body.Add });
    }

    // ---- upkeep -----------------------------------------------------------------------------

    private void MaintainMedia(ChatRoom room, long now)
    {
        long edge = (long)_options.MediaEdgeTimeout.TotalMilliseconds;
        bool removedAny = false;
        foreach (var g in room.Media.Values.ToList())
        {
            if (!g.IsRoot && now - g.AnnSeenAt > MediaLifetimeMs)
            {
                RemoveMediaGroup(g);
                removedAny = true;
                continue;
            }
            if (g.IsRoot && now - g.LastAnnouncedAt >= _options.MediaAnnounceInterval.TotalMilliseconds) SendMediaAnnounce(room, g, active: true);

            List<PeerId> deadChildren = new(), neighbours;
            PeerId? deadParent = null;
            lock (g.Gate)
            {
                if (!g.Joined && !g.IsRoot) continue;
                foreach (var (id, child) in g.Children)
                    if (now - child.HeardAt > edge) deadChildren.Add(id);
                foreach (var id in deadChildren) g.Children.Remove(id);
                if (g.Parent is { } parent && now - g.ParentHeardAt > edge)
                {
                    deadParent = parent;
                    g.Parent = null;
                    g.Exclude.Add(parent);
                    g.ExcludeSince = now;
                    g.Placed = null;
                    g.LastRequestAt = 0;
                }
                neighbours = Neighbours(g);
            }
            foreach (var id in deadChildren) ReportLink(g, id, add: false, 0);
            if (deadParent is { } lost) SendMedia(lost, g, new WireMedia { Op = "leave" });
            foreach (var id in neighbours) SendMedia(id, g, new WireMedia { Op = "ping" });
            AdvanceMedia(g);
        }
        if (removedAny) room.RaiseMediaChanged();
    }

    // A connection closed: if it was a neighbour in a tree, deal with it now instead of waiting for the timeout.
    private void MediaPeerGone(PeerId id)
    {
        foreach (var room in _rooms.Values)
            foreach (var g in room.Media.Values)
            {
                bool wasChild, wasParent = false;
                lock (g.Gate)
                {
                    wasChild = g.Children.Remove(id);
                    if (g.Parent == id)
                    {
                        wasParent = true;
                        g.Parent = null;
                        g.Exclude.Add(id);
                        g.ExcludeSince = Environment.TickCount64;
                        g.Placed = null;
                        g.LastRequestAt = 0;
                    }
                }
                if (wasChild) ReportLink(g, id, add: false, 0);
                if (wasParent) AdvanceMedia(g);
            }
    }

    // ---- the packets ------------------------------------------------------------------------

    internal void PublishMedia(MediaGroup g, ReadOnlySpan<byte> payload, bool isKey)
    {
        if (payload.Length == 0 || payload.Length > MediaWire.MaxPayload(g.Info.Signed)) throw new ArgumentException("The packet is empty or too large.", nameof(payload));
        if (g.Info.Signed && !g.IsRoot) throw new InvalidOperationException("Only the sharer sends in this stream.");
        if (!g.Info.Upstream && !g.IsRoot) throw new InvalidOperationException("Only the sharer sends in this stream.");
        uint seq;
        lock (g.Gate) seq = ++g.Seq;
        var packet = MediaWire.Pack(g.Tag, MediaWire.Origin8(Id), seq, MediaWire.DefaultHops, isKey, payload, g.Info.Signed ? _identity : null);
        Remember(g, packet, isKey);
        SendToNeighbours(g, packet, except: null, charge: false);
    }

    private void SendToNeighbours(MediaGroup g, byte[] packet, PeerId? except, bool charge)
    {
        List<PeerId> targets;
        lock (g.Gate) targets = Neighbours(g);
        foreach (var id in targets)
        {
            if (id == except || !TryPeer(id, out var state)) continue;
            if (charge && !MediaBudget.TryTake(packet.Length, Environment.TickCount64))
            {
                lock (g.Gate) g.Dropped++;
                continue;
            }
            try { state.Connection.SendUnreliable(ChannelIds.Media, packet); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { continue; }
            if (charge) lock (g.Gate) g.Forwarded++;
        }
    }

    // The packets since the last key, so a newcomer can start straight away without asking the sharer for another key.
    private void Remember(MediaGroup g, byte[] packet, bool isKey)
    {
        if (g.Kind == "voice") return;
        lock (g.Gate)
        {
            if (isKey) { g.Cache.Clear(); g.CacheBytes = 0; }
            else if (g.Cache.Count == 0) return;
            if (g.CacheBytes + packet.Length > MaxCacheBytes) { g.Cache.Clear(); g.CacheBytes = 0; return; }
            g.Cache.Enqueue(packet);
            g.CacheBytes += packet.Length;
        }
    }

    private void ReplayCache(MediaGroup g, PeerState to)
    {
        byte[][] packets;
        lock (g.Gate) packets = g.Cache.ToArray();
        foreach (var p in packets)
        {
            try { to.Connection.SendUnreliable(ChannelIds.Media, p); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; }
        }
    }

    private void OnMediaData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (reliable || conn.RemoteId is not { } from) return;
        var span = data.Span;
        if (!MediaWire.TryParseHeader(span, out var tag, out var origin, out var seq, out var hops, out var isKey)) return;
        if (!_mediaByTag.TryGetValue(tag, out var g)) return;

        int tail = g.Info.Signed ? MediaWire.SignatureSize : 0;
        if (span.Length <= MediaWire.HeaderSize + tail) return;
        bool oneWay = !g.Info.Upstream;
        lock (g.Gate)
        {
            // Only our neighbours in the tree may send us this, and in a one-way stream only the root may be the source.
            if (g.Parent != from && !g.Children.ContainsKey(from)) return;
            if (oneWay && origin != MediaWire.Origin8(g.Info.Root)) return;
        }
        if (g.Info.Signed)
        {
            var signed = MediaWire.SignedBytes(span[..(span.Length - tail)]);
            if (!PeerIdentity.Verify(g.RootPub, signed, span[^tail..])) return;
        }

        lock (g.Gate)
        {
            if (!g.Windows.TryGetValue(origin, out var window)) g.Windows[origin] = window = new SeqWindow();
            if (!window.Accept(seq)) { g.Duplicates++; return; }
            if (g.Parent == from) g.ParentHeardAt = Environment.TickCount64;
            else if (g.Children.TryGetValue(from, out var child)) child.HeardAt = Environment.TickCount64;
        }

        var packet = data.ToArray();
        Remember(g, packet, isKey);

        Action<MediaPacket>? deliver;
        bool joined;
        lock (g.Gate) { deliver = g.Deliver; joined = g.Joined; }
        if (joined && deliver != null && ResolveMediaOrigin(g, origin) is { } who && who != Id && !IsBlocked(who))
        {
            var payload = new ReadOnlyMemory<byte>(packet, MediaWire.HeaderSize, packet.Length - MediaWire.HeaderSize - tail);
            lock (g.Gate) g.Delivered++;
            try { deliver(new MediaPacket(who, payload, isKey)); }
            catch (Exception ex) { _options.Log?.Invoke($"a stream handler failed: {ex.Message}"); }
        }

        if (hops > 1)
        {
            var onward = (byte[])packet.Clone();
            onward[17] = (byte)(hops - 1);
            SendToNeighbours(g, onward, except: from, charge: true);
        }
    }

    // Packets carry a short form of the sender's id; find who it is among the people we know in the room.
    private PeerId? ResolveMediaOrigin(MediaGroup g, ulong origin)
    {
        lock (g.Gate) if (g.Origins.TryGetValue(origin, out var known)) return known;
        PeerId? found = null;
        if (MediaWire.Origin8(g.Info.Root) == origin) found = g.Info.Root;
        else if (MediaWire.Origin8(Id) == origin) found = Id;
        else
        {
            var room = g.Room;
            var candidates = room.MemberStates.Keys.ToList();
            lock (room.IndirectGate)
            {
                candidates.AddRange(room.Voice.Keys);
                candidates.AddRange(room.Presence.Keys);
            }
            candidates.AddRange(room.Indirect.Keys);
            foreach (var id in candidates)
                if (MediaWire.Origin8(id) == origin) { found = id; break; }
        }
        if (found is { } f) lock (g.Gate) g.Origins[origin] = f;
        return found;
    }
}
