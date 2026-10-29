using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

// Members with a good connection relay for the others in their rooms. Nobody opts in or out: whether you relay
// follows from what your network can do, so rooms get relays without anyone having to volunteer.
public sealed partial class ChatNode
{
    private int _announcedRelay = -1;

    /// <summary>For tests: pretend to have (or lack) the kind of connection that relays, whatever the network says.</summary>
    internal bool? RelayCapableOverride { get; set; }

    /// <summary>
    /// True when this node relays for other members: it has a public address, an open port mapping or a router that
    /// keeps the same public port for every destination, so two members who cannot reach each other can both reach it.
    /// </summary>
    public bool IsRelayCapable
    {
        get
        {
            if (!_options.EnableRelaying) return false;
            if (RelayCapableOverride is { } forced) return forced;
            var status = NetworkStatus;
            return status.Mapping != null || status.Nat.Type is NatType.Open or NatType.EndpointIndependent;
        }
    }

    /// <summary>What this node has forwarded for other members, and what its limits turned away.</summary>
    public RelayStats RelayStats => Network.RelayStats;

    private bool MaySharedRoomRelay(PeerConnection a, PeerConnection b) =>
        IsRelayCapable && a.RemoteId is { } x && b.RemoteId is { } y && !IsBlocked(x) && !IsBlocked(y) &&
        _rooms.Values.Any(r => r.MemberStates.ContainsKey(x) && r.MemberStates.ContainsKey(y));

    /// <summary>Tells everyone we are connected to whether we relay, when that has changed since last time.</summary>
    internal void AnnounceRelayCapability()
    {
        int now = IsRelayCapable ? 1 : 0;
        if (Interlocked.Exchange(ref _announcedRelay, now) == now) return;
        _options.Log?.Invoke(now == 1 ? "your connection can relay for other members of your rooms" : "no longer relaying for other members");
        foreach (var peer in _peers.Values)
            Send(peer.Connection, new Wire { Type = Wire.Caps, Rly = now == 1 });
    }

    /// <summary>
    /// The connected members who could carry traffic to this person: those that introduced them and are known to relay,
    /// then those that have not said (older versions). Members known not to relay are never asked.
    /// </summary>
    private List<PeerConnection> RelayCandidates(PeerId target)
    {
        var ids = new HashSet<PeerId>();
        if (_introducedVia.TryGetValue(target, out var vias)) ids.UnionWith(vias.Keys);
        // Also anyone who says they are connected to the target, so a path can move to another member when the first leaves.
        foreach (var p in _peers.Values)
            if (_rooms.Keys.Any(k => p.Links(k).ContainsKey(target.Value))) ids.Add(p.Id);
        return ids
            .Select(id => _peers.GetValueOrDefault(id))
            .Where(p => p is { CanRelay: not false } && p.Connection is { IsRelayed: false, State: ConnectionState.Established })
            .OrderBy(p => p!.CanRelay == true ? 0 : 1)
            .Select(p => p!.Connection)
            .ToList();
    }

    // Someone connecting to us through a member is accepted if that member introduced them, or says it is connected to them.
    // The handshake still proves who they are, so this only decides whether we answer at all.
    private bool MayAcceptRelayed(PeerConnection via, PeerId from) =>
        via.RemoteId is { } v && !IsBlocked(from) && !IsBlocked(v) &&
        ((_introducedVia.TryGetValue(from, out var vias) && vias.ContainsKey(v)) ||
         (_peers.TryGetValue(v, out var state) && _rooms.Keys.Any(k => state.Links(k).ContainsKey(from.Value))));

    private const long StandbyRetryMs = 10_000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<PeerId, long> _standbyTried = new();

    /// <summary>
    /// Someone we reach through a member keeps a second path ready through another member. If the first member
    /// disappears, the second takes over at once, with no new handshake and nothing for the person to notice.
    /// </summary>
    private void EnsureStandby(PeerState peer)
    {
        var conn = peer.Connection;
        if (!_options.EnableRelaying || !conn.IsRelayed || conn.State != ConnectionState.Established || Network.HasStandby(peer.Id)) return;
        long now = Environment.TickCount64;
        if (_standbyTried.TryGetValue(peer.Id, out var last) && now - last < StandbyRetryMs) return;
        var relay = RelayCandidates(peer.Id).FirstOrDefault(c => c.RemoteId != conn.RelayedVia);
        if (relay == null) return;
        _standbyTried[peer.Id] = now;
        _options.Log?.Invoke($"keeping a standby path to {peer.Id.Short} via {relay.RemoteId!.Value.Short}");
        Network.ConnectStandby(relay, peer.Id);
    }

    private void OnStandbyReady(PeerConnection standby)
    {
        if (standby.RemoteId is { } id && _peers.TryGetValue(id, out var state)) state.KnowSession(standby.SessionId);
    }

    // The path in use was lost and the standby took over. Same person, same state: only the connection under it changed.
    private void OnConnectionPromoted(PeerConnection lost, PeerConnection now)
    {
        if (now.RemoteId is not { } id || !_peers.TryGetValue(id, out var state)) return;
        state.KnowSession(lost.SessionId);
        state.KnowSession(now.SessionId);
        state.Connection = now;
        _options.Log?.Invoke($"{id.Short}: path via {lost.RelayedVia?.Short ?? "direct"} lost; continuing via {now.RelayedVia?.Short} (standby)");
        // Anything sent on the lost path and not yet confirmed goes again on the new one.
        ResendTo(state, o => o.DirectPeer == id, o => o.DirectWire!);
        foreach (var room in _rooms.Values)
            if (room.MemberStates.ContainsKey(id))
                ResendTo(state, o => o.Room == room, o => new Wire { Type = Wire.Message, Room = RefTo(room, state), Msgs = new[] { o.Signed! } });
    }

    /// <summary>True while we should keep trying a path through another member: nothing works yet, or only the shared TURN server does.</summary>
    private bool WantsPeerRelay(PeerId target) =>
        !_peers.TryGetValue(target, out var state) || (!state.Connection.IsRelayed && IsOnSharedServer(state.Connection));

    /// <summary>For tests: say which addresses belong to the shared server.</summary>
    internal Func<System.Net.IPEndPoint, bool>? SharedServerAddressOverride { get; set; }

    /// <summary>
    /// True for an address on the shared TURN server (where people's relay addresses are) when we are already
    /// connected to whoever it belongs to, however that connection runs. Dialling it again would only make the
    /// server carry a second path, or tear down the one through another member.
    /// </summary>
    internal bool IsRedundantServerPath(System.Net.IPEndPoint endPoint, PeerId? id)
    {
        bool onServer = SharedServerAddressOverride?.Invoke(endPoint)
            ?? (_turn?.ServerEndPoint is { } server && server.Address.Equals(endPoint.Address));
        if (!onServer) return false;
        var who = id ?? Network.OwnerOf(endPoint);
        return who is { } w && _peers.ContainsKey(w);
    }

    /// <summary>For tests: say which connections run through the shared server.</summary>
    internal Func<PeerConnection, bool>? OnSharedServerOverride { get; set; }

    private bool IsOnSharedServer(PeerConnection conn) =>
        OnSharedServerOverride?.Invoke(conn) ?? (_turn is { } turn && !conn.IsRelayed && turn.UsesRelayFor(conn.RemoteEndPoint));
}
