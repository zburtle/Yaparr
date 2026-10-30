using System.Collections.Concurrent;
using System.Security.Cryptography;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

internal sealed class ChildLink
{
    public long HeardAt;
    public int Cap;
    public long LastReplayAt;
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

    /// <summary>Called at the root when a viewer needs a new key frame.</summary>
    public Action? KeyRequested;
    public long LastKeyRequestAt;

    /// <summary>The stream is too big for the shared server, and every way we could reach the tree runs through it.</summary>
    public bool ServerBlocked;
    public int ServerRefusals;

    // Packets recently seen, to give again to a child that missed them; and what we are missing ourselves.
    public readonly Dictionary<uint, byte[]> Recent = new();
    public readonly Queue<uint> RecentOrder = new();
    public bool SeenAny, NackRunning;
    public uint HighestSeq;
    public readonly Dictionary<uint, long> Missing = new();
    public readonly Dictionary<uint, long> Asked = new();

    // A replay from the parent: which numbers it promised (first to last), which have come, and when the last one did.
    public bool ReplayExpected, ReplayChecking;
    public uint ReplayFirst, ReplayLast, ReplayMax;
    public HashSet<uint> ReplayGot = new();
    public readonly Dictionary<uint, long> ReplayAsked = new();
    public long LastReplayPacketAt;
    public long Retransmitted, Recovered;

    public uint Seq;
    public readonly Dictionary<ulong, SeqWindow> Windows = new();
    public readonly Dictionary<ulong, PeerId> Origins = new();
    // The packets since the last key, by number, for a viewer that joins late or has to start over.
    public readonly Dictionary<uint, byte[]> Cache = new();
    public int CacheBytes;
    public bool CacheHasKey;
    public uint CacheKeySeq, CacheMaxSeq;

    /// <summary>True when the cache holds every packet from the key to the newest one, with no hole.</summary>
    public bool CacheUsable => CacheHasKey && Cache.Count == (int)(CacheMaxSeq - CacheKeySeq) + 1;
    public long Forwarded, Delivered, Dropped, Duplicates;

    public string Kind => Info.Kind;
    public bool Attached => IsRoot || Parent != null;
}

/// <summary>The sending end of a stream you share with a room. Dispose it to stop sharing.</summary>
public sealed class MediaSource : IDisposable
{
    private readonly ChatNode _node;
    private readonly MediaGroup _group;
    private uint _frameId;
    private Timer? _probe;
    private long _lastSendAt;
    private int _probesLeft;

    internal MediaSource(ChatNode node, MediaGroup group)
    {
        _node = node;
        _group = group;
        group.KeyRequested = () => KeyFrameRequested?.Invoke();
    }

    public MediaGroupInfo Info => _group.Info;

    /// <summary>The most one packet can carry.</summary>
    public int MaxPayload => MediaWire.MaxPayload(_group.Info.Signed);

    /// <summary>Sends one packet to everyone watching. Mark the first packet of each independently decodable section as a key.</summary>
    public void Send(ReadOnlySpan<byte> payload, bool isKey = false) => _node.PublishMedia(_group, payload, isKey);

    /// <summary>
    /// Sends one frame of any size (up to 4 MB): it is cut into packets, spread out over time so a big key frame does not
    /// arrive as one burst, and put back together by <see cref="MediaView.FrameReceived"/>. A key frame is one a viewer can
    /// start from; send one first, and again whenever <see cref="KeyFrameRequested"/> is raised.
    /// </summary>
    public async Task SendFrameAsync(ReadOnlyMemory<byte> frame, bool isKey, CancellationToken ct = default)
    {
        if (frame.Length == 0 || frame.Length > Fragments.MaxFrameBytes) throw new ArgumentException("A frame is between 1 byte and 4 MB.", nameof(frame));
        int count = Fragments.Count(frame.Length, MaxPayload);
        int per = MaxPayload - Fragments.HeaderSize;
        uint id = Interlocked.Increment(ref _frameId);
        // Fast enough for a key frame to go out in a fraction of a second, slow enough not to overflow the buffers on the way.
        double rate = Math.Max(256 * 1024, _group.Info.Kbps * 125.0 * 4);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long sent = 0;
        for (int i = 0; i < count; i++)
        {
            var piece = frame.Slice(i * per, Math.Min(per, frame.Length - i * per));
            _node.PublishMedia(_group, Fragments.Pack(id, i, count, isKey, piece.Span), isKey && i == 0);
            sent += piece.Length + MediaWire.HeaderSize + Fragments.HeaderSize;
            double ahead = sent * 1000.0 / rate - System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ahead > 2) await Task.Delay((int)ahead, ct).ConfigureAwait(false);
        }
        ArmProbe();
    }

    // Receivers only notice a lost packet when a later one arrives. After the last frame nothing does, so two tiny
    // packets follow a moment later; they carry numbers, and so show what is missing before them.
    private void ArmProbe()
    {
        Volatile.Write(ref _lastSendAt, Environment.TickCount64);
        Volatile.Write(ref _probesLeft, 2);
        (_probe ??= new Timer(_ => Probe(), null, Timeout.Infinite, Timeout.Infinite)).Change(80, Timeout.Infinite);
    }

    private void Probe()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastSendAt) < 70) return;
        try { _node.PublishMedia(_group, new byte[] { 0 }, false); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        if (Interlocked.Decrement(ref _probesLeft) > 0) _probe?.Change(80, Timeout.Infinite);
    }

    /// <summary>A viewer lost something it cannot do without; send a key frame soon. Raised at most once a second.</summary>
    public event Action? KeyFrameRequested;

    public MediaStatus Status => _node.StatusOf(_group);

    public void Dispose()
    {
        _probe?.Dispose();
        _node.StopMedia(_group);
    }
}

/// <summary>Watching a stream someone shares with the room. Dispose it to stop.</summary>
public sealed class MediaView : IDisposable
{
    private readonly ChatNode _node;
    private readonly MediaGroup _group;
    private readonly object _gate = new();
    private FrameAssembler? _assembler;
    private Timer? _timer;
    private Action<MediaFrame>? _frame;

    internal MediaView(ChatNode node, MediaGroup group)
    {
        _node = node;
        _group = group;
        lock (group.Gate) { group.Joined = true; group.Deliver = OnPacket; }
    }

    public MediaGroupInfo Info => _group.Info;

    /// <summary>A packet arrived. It can come from any thread.</summary>
    public event Action<MediaPacket>? Received;

    /// <summary>
    /// A whole frame arrived (for streams sent with <see cref="MediaSource.SendFrameAsync"/>), in order and without gaps.
    /// After a loss nothing is delivered until the next key frame, which this asks the sharer for.
    /// </summary>
    public event Action<MediaFrame>? FrameReceived
    {
        add { lock (_gate) { _frame += value; _assembler ??= StartFraming(); } }
        remove { lock (_gate) _frame -= value; }
    }

    public long FramesDelivered { get { lock (_gate) return _assembler?.Delivered ?? 0; } }
    public long FramesLost { get { lock (_gate) return _assembler?.Lost ?? 0; } }

    public MediaStatus Status => _node.StatusOf(_group);

    private FrameAssembler StartFraming()
    {
        _timer = new Timer(_ => Feed(null), null, 100, 100);
        return new FrameAssembler(reorderWaitMs: 400, Environment.TickCount64);
    }

    private void OnPacket(MediaPacket packet)
    {
        Received?.Invoke(packet);
        Feed(packet);
    }

    private void Feed(MediaPacket? packet)
    {
        bool ask;
        lock (_gate)
        {
            if (_assembler == null) return;
            var ready = new List<MediaFrame>();
            long now = Environment.TickCount64;
            ask = packet != null ? _assembler.Add(packet.Origin, packet.Payload.Span, now, ready) : _assembler.Tick(now, ready);
            foreach (var frame in ready)
            {
                try { _frame?.Invoke(frame); }
                catch (Exception) { }
            }
        }
        if (ask) _node.RequestKeyFrame(_group);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _node.LeaveMedia(_group);
    }
}

// Streams that many people take part in, such as a shared screen or a large voice channel, cannot be sent to each
// person from one machine. They are passed along a tree instead: the sharer (the root) sends to a few members, and
// each of those passes it on to a few more. The root decides where each newcomer attaches, which keeps the tree
// loop-free and shallow, and members who lose their parent just ask the root again.
public sealed partial class ChatNode
{
    private const int MaxMediaGroupsPerRoom = 16;
    private const long PlaceValidMs = 10_000, RequestGapMs = 300, ExcludeMs = 30_000, DialGiveUpMs = 8_000, ServerBlockedRetryMs = 10_000;
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
    /// <param name="kbps">The bitrate the stream needs, in kilobits per second. Streams above <see cref="ChatNodeOptions.MaxMeteredStreamKbps"/> are not carried over the shared relay server.</param>
    public MediaSource StartMedia(ChatRoom room, string kind, string name, bool signed = true, bool upstream = false, int? fanout = null, int kbps = 1000)
    {
        if (!_rooms.ContainsKey(room.Key)) throw new InvalidOperationException("You are not in that room.");
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 16 || kind == "voice") throw new ArgumentException("Choose another kind of stream.", nameof(kind));
        return new MediaSource(this, CreateRootGroup(room, kind, name, signed, upstream, fanout ?? _options.MediaRootFanout, kbps));
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
            return new MediaStatus(g.Id, g.IsRoot, g.Parent, g.Children.Keys.ToList(), g.Delivered, g.Forwarded, g.Dropped, g.Duplicates, g.Retransmitted, g.Recovered, g.ServerBlocked);
    }

    // ---- starting, announcing, ending -------------------------------------------------------

    private MediaGroup CreateRootGroup(ChatRoom room, string kind, string name, bool signed, bool upstream, int fanout, int kbps)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var ann = SignedMedia.Create(_identity, room.Key, kind, id, name, NextTimestamp(), active: true, Math.Clamp(fanout, 1, 64), signed, upstream, Math.Clamp(kbps, 0, 100_000));
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
            Info = new MediaGroupInfo(ann.Group, ann.Kind, ann.Name, new PeerId(ann.From), ann.Signed, ann.Upstream, ann.Kbps),
            AnnSeenAt = Environment.TickCount64,
        };
        if (!_mediaByTag.TryAdd(g.Tag, g)) return null;
        if (!room.Media.TryAdd(g.Id, g)) { _mediaByTag.TryRemove(g.Tag, out _); return null; }
        return g;
    }

    private void SendMediaAnnounce(ChatRoom room, MediaGroup g, bool active)
    {
        var ann = SignedMedia.Create(_identity, room.Key, g.Info.Kind, g.Id, g.Info.Name, NextTimestamp(), active, g.RootFanout, g.Info.Signed, g.Info.Upstream, g.Info.Kbps);
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

    private bool TooBigForServer(MediaGroup g, PeerConnection conn) =>
        g.Info.Kbps > _options.MaxMeteredStreamKbps && IsOnSharedServer(conn);

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
            // Having found that only the shared server would carry it, ask again only now and then.
            if (now - g.LastRequestAt < (g.ServerBlocked ? ServerBlockedRetryMs : RequestGapMs)) return;
            g.LastRequestAt = now;
            if (g.Placed != null && now - g.PlacedAt > PlaceValidMs) g.Placed = null;
            placed = g.Placed;
            exclude = g.Exclude.Select(x => x.Value).ToList();
        }

        if (placed is { } parent)
        {
            if (!TryPeer(parent, out var parentState))
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
            // A stream too big for the shared server is not carried over it: look for a parent we reach another way.
            if (TooBigForServer(g, parentState.Connection))
            {
                lock (g.Gate)
                {
                    g.Exclude.Add(parent);
                    g.ExcludeSince = now;
                    g.Placed = null;
                    if (++g.ServerRefusals >= 2) g.ServerBlocked = true;
                }
                return;
            }
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
            case "key": OnMediaKey(peer, g); break;
            case "nack": OnMediaNack(peer, g, m); break;
            case "replay": OnMediaReplay(peer, g, m); break;
        }
    }

    // A viewer needs a place to start from. The cache of whoever is closest may have one; if not, the sharer has to make one.
    internal void RequestKeyFrame(MediaGroup g)
    {
        PeerId? up;
        bool root;
        lock (g.Gate)
        {
            long now = Environment.TickCount64;
            if (now - g.LastKeyRequestAt < 1000) return;
            g.LastKeyRequestAt = now;
            up = g.Parent;
            root = g.IsRoot;
        }
        if (root) g.KeyRequested?.Invoke();
        else if (up is { } parent) SendMedia(parent, g, new WireMedia { Op = "key" });
    }

    private void OnMediaKey(PeerState peer, MediaGroup g)
    {
        bool known, hasCache, replay = false;
        lock (g.Gate)
        {
            known = g.Children.TryGetValue(peer.Id, out var child);
            hasCache = g.CacheUsable;
            long now = Environment.TickCount64;
            if (known && hasCache && now - child!.LastReplayAt > 1500) { child.LastReplayAt = now; replay = true; }
        }
        if (!known) return;
        if (replay) ReplayCache(g, peer);
        // A copy was given a moment ago and is still arriving. Only when ours has holes, or is gone, the request goes up
        // to someone whose copy is whole, and in the end to the sharer.
        else if (!hasCache) RequestKeyFrame(g);
    }

    // A child is missing packets we may still have. For a replay (Add set) they are marked so the child takes them even
    // if it saw the numbers before.
    private void OnMediaNack(PeerState peer, MediaGroup g, WireMedia m)
    {
        if (m.Seqs == null) return;
        var found = new List<byte[]>();
        lock (g.Gate)
        {
            if (!g.Children.ContainsKey(peer.Id)) return;
            foreach (var seq in m.Seqs.Take(32))
            {
                if (g.Recent.TryGetValue(seq, out var packet) || g.Cache.TryGetValue(seq, out packet))
                {
                    if (m.Add) { packet = (byte[])packet.Clone(); packet[0] |= MediaWire.ReplayFlag; }
                    found.Add(packet);
                }
            }
        }
        foreach (var packet in found)
        {
            if (!MediaBudget.TryTake(packet.Length, Environment.TickCount64)) { lock (g.Gate) g.Dropped++; continue; }
            try { peer.Connection.SendUnreliable(ChannelIds.Media, packet); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; }
            lock (g.Gate) g.Retransmitted++;
        }
    }

    // Notes which packets of a one-way stream have arrived, and asks our parent for the ones that did not.
    private void TrackSeq(MediaGroup g, uint seq)
    {
        bool gap = false;
        lock (g.Gate)
        {
            long now = Environment.TickCount64;
            if (!g.SeenAny) { g.SeenAny = true; g.HighestSeq = seq; return; }
            int ahead = (int)(seq - g.HighestSeq);
            if (ahead > 0)
            {
                if (ahead <= 256)
                    for (uint s = g.HighestSeq + 1; s != seq; s++) { g.Missing[s] = now; gap = true; }
                g.HighestSeq = seq;
            }
            else if (g.Missing.Remove(seq)) g.Recovered++;
        }
        if (gap) ScheduleNack(g);
    }

    private void ScheduleNack(MediaGroup g)
    {
        lock (g.Gate)
        {
            if (g.NackRunning) return;
            g.NackRunning = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                for (int round = 0; ; round++)
                {
                    await Task.Delay(40, _cts.Token).ConfigureAwait(false);
                    List<uint> want;
                    PeerId? parent;
                    lock (g.Gate)
                    {
                        long now = Environment.TickCount64;
                        foreach (var old in g.Missing.Where(kv => now - kv.Value > 1500).Select(kv => kv.Key).ToList()) { g.Missing.Remove(old); g.Asked.Remove(old); }
                        // Ending and clearing the flag happen together, so a gap noticed from now on starts a new round.
                        if (g.Missing.Count == 0) { g.NackRunning = false; g.Asked.Clear(); return; }
                        // Not again until the answer has had time to come.
                        want = g.Missing.Where(kv => now - kv.Value >= 30 && now - g.Asked.GetValueOrDefault(kv.Key) >= 80).Select(kv => kv.Key).Take(32).ToList();
                        foreach (var seq in want) g.Asked[seq] = now;
                        parent = g.Parent;
                    }
                    if (want.Count > 0 && parent is { } p) SendMedia(p, g, new WireMedia { Op = "nack", Seqs = want.ToArray() });
                }
            }
            catch (OperationCanceledException) { lock (g.Gate) g.NackRunning = false; }
        });
    }

    // A replay is arriving: ask for the numbers it promised and left out. The packets come in order, so a number that is
    // missing below the highest one that has come is lost; waiting for the end would be too late, because the viewer
    // gives up on a frame after a quarter of a second. What is left at the end is asked for once it has gone quiet.
    private void OnMediaReplay(PeerState peer, MediaGroup g, WireMedia m)
    {
        if (m.Seqs is not { Length: 2 }) return;
        lock (g.Gate)
        {
            if (g.Parent != peer.Id) return;
            g.ReplayExpected = true;
            g.ReplayFirst = m.Seqs[0];
            g.ReplayLast = m.Seqs[1];
            g.ReplayMax = m.Seqs[0] - 1;
            g.ReplayGot = new HashSet<uint>();
            g.ReplayAsked.Clear();
            g.LastReplayPacketAt = Environment.TickCount64;
            if (g.ReplayChecking) return;
            g.ReplayChecking = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                long started = Environment.TickCount64;
                while (Environment.TickCount64 - started < 5000)
                {
                    await Task.Delay(50, _cts.Token).ConfigureAwait(false);
                    List<uint> missing = new();
                    PeerId? parent;
                    bool finished = false;
                    lock (g.Gate)
                    {
                        long now = Environment.TickCount64;
                        bool quiet = now - g.LastReplayPacketAt >= 120;
                        // While it arrives, only what is behind the newest number; at the end, everything promised.
                        uint limit = quiet ? g.ReplayLast : g.ReplayMax - 2;
                        for (uint seq = g.ReplayFirst; (int)(limit - seq) >= 0 && missing.Count < 32; seq++)
                            if (!g.ReplayGot.Contains(seq) && now - g.ReplayAsked.GetValueOrDefault(seq) >= 100) missing.Add(seq);
                        foreach (var seq in missing) g.ReplayAsked[seq] = now;
                        parent = g.Parent;
                        finished = quiet && missing.Count == 0 && !g.ReplayAsked.Keys.Any(q => !g.ReplayGot.Contains(q));
                    }
                    if (missing.Count > 0 && parent is { } p) SendMedia(p, g, new WireMedia { Op = "nack", Add = true, Seqs = missing.ToArray() });
                    if (finished) break;
                }
            }
            catch (OperationCanceledException) { }
            finally { lock (g.Gate) { g.ReplayExpected = false; g.ReplayChecking = false; } }
        });
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
            if (parent == null)
            {
                // No room anywhere that we have not ruled out. The ones we gave up on may be fine again (a network
                // blip looks like a dead parent), and they are all there is, so they get another chance.
                g.Placed = null;
                g.Exclude.Clear();
                g.LastRequestAt = now + 1500;
                return;
            }
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
            accept = (g.IsRoot || g.Parent != null) && peer.Id != g.Parent && (already || g.Children.Count < FanoutOf(g))
                && !TooBigForServer(g, peer.Connection);
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
                g.ServerBlocked = false;
                g.ServerRefusals = 0;
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
        Remember(g, packet, seq, isKey);
        StoreRecent(g, seq, packet);
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
    private void Remember(MediaGroup g, byte[] packet, uint seq, bool isKey)
    {
        if (g.Kind == "voice") return;
        lock (g.Gate)
        {
            if (isKey)
            {
                // A key that turns up late (given again after a loss) is not a new start.
                if (g.CacheHasKey && (int)(seq - g.CacheKeySeq) <= 0) return;
                g.Cache.Clear();
                g.CacheBytes = 0;
                g.CacheHasKey = true;
                g.CacheKeySeq = g.CacheMaxSeq = seq;
            }
            else if (!g.CacheHasKey || (int)(seq - g.CacheKeySeq) < 0) return;
            if (g.Cache.ContainsKey(seq)) return;
            if (g.CacheBytes + packet.Length > MaxCacheBytes) { g.Cache.Clear(); g.CacheBytes = 0; g.CacheHasKey = false; return; }
            g.Cache[seq] = packet;
            g.CacheBytes += packet.Length;
            if ((int)(seq - g.CacheMaxSeq) > 0) g.CacheMaxSeq = seq;
        }
    }

    // The last few hundred packets of a one-way stream, for children that missed one.
    private void StoreRecent(MediaGroup g, uint seq, byte[] packet)
    {
        if (g.Info.Upstream) return;
        lock (g.Gate)
        {
            if (!g.Recent.TryAdd(seq, packet)) return;
            g.RecentOrder.Enqueue(seq);
            while (g.RecentOrder.Count > 600) g.Recent.Remove(g.RecentOrder.Dequeue());
        }
    }

    // Gives a neighbour the packets since the last key, a few at a time: a long burst overflows the receiver's buffer
    // and loses the very packets that were meant to make up for a loss.
    private void ReplayCache(MediaGroup g, PeerState to)
    {
        byte[][] packets;
        uint first, last;
        lock (g.Gate)
        {
            if (!g.CacheUsable) return;
            packets = g.Cache.OrderBy(kv => (int)(kv.Key - g.CacheKeySeq)).Select(kv => kv.Value).ToArray();
            first = g.CacheKeySeq;
            last = g.CacheMaxSeq;
        }
        if (packets.Length == 0) return;
        // Said first on the reliable connection, so the other side knows exactly which numbers to expect.
        SendMedia(to.Id, g, new WireMedia { Op = "replay", Seqs = new[] { first, last } });
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < packets.Length; i++)
                {
                    // Marked as a replay: the receiver may have had these numbers before, and needs them again to start over.
                    var packet = (byte[])packets[i].Clone();
                    packet[0] |= MediaWire.ReplayFlag;
                    try { to.Connection.SendUnreliable(ChannelIds.Media, packet); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return; }
                    if ((i + 1) % 12 == 0) await Task.Delay(10, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnMediaData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (reliable || conn.RemoteId is not { } from) return;
        var span = data.Span;
        if (!MediaWire.TryParseHeader(span, out var tag, out var origin, out var seq, out var hops, out var isKey, out var isReplay)) return;
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
            if (!isReplay && !window.Accept(seq)) { g.Duplicates++; return; }
            if (g.Parent == from) g.ParentHeardAt = Environment.TickCount64;
            else if (g.Children.TryGetValue(from, out var child)) child.HeardAt = Environment.TickCount64;
        }

        var packet = data.ToArray();
        // What we keep for others is the packet as it was first sent, not as it happened to reach us.
        if (isReplay)
        {
            packet[0] &= unchecked((byte)~MediaWire.ReplayFlag);
            lock (g.Gate)
            {
                if (g.Missing.Remove(seq)) g.Recovered++;
                if (g.ReplayExpected)
                {
                    g.ReplayGot.Add(seq);
                    g.LastReplayPacketAt = Environment.TickCount64;
                    if ((int)(seq - g.ReplayMax) > 0 && (int)(seq - g.ReplayLast) <= 0) g.ReplayMax = seq;
                }
            }
        }
        Remember(g, packet, seq, isKey);
        StoreRecent(g, seq, packet);
        if (!g.Info.Upstream && !isReplay) TrackSeq(g, seq);

        Action<MediaPacket>? deliver;
        bool joined;
        lock (g.Gate) { deliver = g.Deliver; joined = g.Joined; }
        if (joined && deliver != null && ResolveMediaOrigin(g, origin) is { } who && who != Id && !IsBlocked(who))
        {
            var payload = new ReadOnlyMemory<byte>(packet, MediaWire.HeaderSize, packet.Length - MediaWire.HeaderSize - tail);
            lock (g.Gate) g.Delivered++;
            try { deliver(new MediaPacket(who, payload, isKey, seq)); }
            catch (Exception ex) { _options.Log?.Invoke($"a stream handler failed: {ex.Message}"); }
        }

        if (hops > 1 && !isReplay)
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
