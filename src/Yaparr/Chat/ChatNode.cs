using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Yaparr.Discovery;
using Yaparr.Discovery.Dht;
using Yaparr.Identity;
using Yaparr.Transport;
using Yaparr.Vpn;

namespace Yaparr.Chat;

public sealed class ChatNodeOptions
{
    public string DisplayName { get; init; } = "anonymous";

    public PeerIdentity? Identity { get; init; }

    public int ListenPort { get; init; }

    public Func<UdpMux, IPeerDiscovery>? DiscoveryFactory { get; init; }

    public bool EnableLocalDiscovery { get; init; } = true;

    public DhtOptions Dht { get; init; } = new();
    public PeerNetworkOptions Network { get; init; } = new();

    public TraversalOptions Traversal { get; init; } = new();

    public WireGuardConfig? Vpn { get; init; }

    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(15);

    public bool EnableRelaying { get; init; } = true;

    public TimeSpan RelayFallbackDelay { get; init; } = TimeSpan.FromSeconds(8);

    public int TargetConnections { get; init; } = 8;

    public int MaxConnections { get; init; } = 12;

    public int IntroductionsPerJoin { get; init; } = 3;

    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ShuffleInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan PresenceInterval { get; init; } = TimeSpan.FromSeconds(30);

    public bool SyncHistory { get; init; } = true;

    public IChatHistoryStore? HistoryStore { get; init; }

    public int MaxHistoryMessages { get; init; } = 500;

    public TimeSpan MaxHistoryAge { get; init; } = TimeSpan.FromDays(7);

    public Action<string>? Log { get; init; }
}

public sealed class ChatNode : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal sealed class PeerState
    {
        private readonly object _gate = new();
        private HashSet<string> _rooms = new();

        public PeerState(PeerConnection connection) => Connection = connection;

        public PeerConnection Connection { get; }
        public long HistoryExpectedUntil;
        private readonly Dictionary<string, long> _lastServed = new();
        public bool TryStartServing(string roomKey)
        {
            lock (_gate)
            {
                long now = Environment.TickCount64;
                if (_lastServed.TryGetValue(roomKey, out var last) && now - last < 5000) return false;
                _lastServed[roomKey] = now;
                return true;
            }
        }
        public string? Name { get; set; }
        public volatile int Degree;
        public PeerId Id => Connection.RemoteId!.Value;
        public PeerInfo Info => new(Id, Name ?? Id.Short, Connection.RemoteEndPoint, Connection.IsRelayed);

        private readonly Dictionary<string, Dictionary<string, string>> _links = new();
        public void SetLinks(string roomKey, Dictionary<string, string> links) { lock (_gate) _links[roomKey] = links; }
        public void ClearLinks(string roomKey) { lock (_gate) _links.Remove(roomKey); }
        public Dictionary<string, string> Links(string roomKey)
        {
            lock (_gate) return _links.TryGetValue(roomKey, out var l) ? new Dictionary<string, string>(l) : new();
        }

        public bool Declares(string reference) { lock (_gate) return _rooms.Contains(reference); }
        public void SetRooms(IEnumerable<string> rooms) { lock (_gate) _rooms = new HashSet<string>(rooms); }
        public bool AddRoom(string key) { lock (_gate) return _rooms.Add(key); }
        public bool RemoveRoom(string key) { lock (_gate) return _rooms.Remove(key); }
    }

    private readonly ChatNodeOptions _options;
    private readonly PeerIdentity _identity;
    private readonly bool _ownsIdentity;
    private readonly UdpMux _mux;
    private readonly List<IPeerDiscovery> _discoveries;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<PeerId, PeerState> _peers = new();
    private readonly ConcurrentDictionary<string, ChatRoom> _rooms = new();
    private readonly ConcurrentDictionary<PeerId, DirectChat> _directChats = new();
    private readonly ConcurrentDictionary<PeerId, ConcurrentDictionary<PeerId, byte>> _introducedVia = new();
    private readonly ConcurrentDictionary<PeerId, byte> _relayPending = new();
    private readonly ConcurrentDictionary<string, RoomHistory> _histories = new();
    private readonly ConcurrentDictionary<string, Candidate> _candidates = new();
    private readonly ConcurrentDictionary<string, long> _backoff = new();
    private readonly ConcurrentDictionary<string, byte> _pinned = new();
    private bool _started;
    private PortMapper? _mapper;
    private TurnClient? _turn;
    private TurnOptions? _turnOptions;
    private WireGuardTunnel? _tunnel;
    private readonly SemaphoreSlim _networkGate = new(1, 1);

    private const int MinDegree = 2;
    private const int MaxCandidates = 512;
    private static readonly long BackoffMs = 60_000;
    private long RedialMs => (long)Math.Max(500, _options.DiscoveryInterval.TotalMilliseconds);

    private sealed class Candidate
    {
        public Candidate(IPEndPoint endPoint) => EndPoint = endPoint;
        public IPEndPoint EndPoint { get; }
        public PeerId? Id { get; set; }
        public PeerId? Via { get; set; }
        public long LastDial;
        public long Seen;
    }

    public ChatNode(ChatNodeOptions? options = null)
    {
        _options = options ?? new ChatNodeOptions();
        _turnOptions = _options.Traversal.Turn;
        _ownsIdentity = _options.Identity == null;
        _identity = _options.Identity ?? PeerIdentity.Generate();
        _mux = new UdpMux(_options.ListenPort) { Log = _options.Log };
        _discoveries = _options.DiscoveryFactory?.Invoke(_mux) is { } custom ? new() { custom } : CreateDefaultDiscoveries();

        Network = new PeerNetwork(_identity, _mux, _options.Network);
        Network.ConnectionEstablished += OnConnectionEstablished;
        Network.ConnectionClosed += OnConnectionClosed;
        Network.RegisterChannel(ChannelIds.Chat, OnChatData);

        if (_options.EnableRelaying)
        {
            Network.RelayForwardPolicy = (a, b) => a.RemoteId is { } x && b.RemoteId is { } y &&
                _rooms.Values.Any(r => r.MemberStates.ContainsKey(x) && r.MemberStates.ContainsKey(y));
            Network.RelayedAcceptPolicy = (via, from) => via.RemoteId is { } v &&
                _introducedVia.TryGetValue(from, out var vias) && vias.ContainsKey(v);
        }
    }

    internal UdpMux Mux => _mux;

    private List<IPeerDiscovery> CreateDefaultDiscoveries()
    {
        var members = new List<IPeerDiscovery> { new DhtDiscovery(_mux, _options.Dht) };
        if (_options.EnableLocalDiscovery)
        {
            try { members.Add(new LocalServiceDiscovery(_mux.LocalPort, _options.Log)); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _options.Log?.Invoke($"local network discovery unavailable: {ex.Message}");
            }
        }
        return members;
    }

    public PeerId Id => _identity.Id;
    public string DisplayName => _options.DisplayName;
    public int LocalPort => _mux.LocalPort;

    public PeerNetwork Network { get; }

    public IPeerDiscovery Discovery => _discoveries.Count == 1 ? _discoveries[0] : new CompositeDiscovery(_discoveries, _options.Log);
    public IReadOnlyCollection<PeerInfo> ConnectedPeers => _peers.Values.Select(p => p.Info).ToList();
    public IReadOnlyCollection<ChatRoom> Rooms => _rooms.Values.ToList();

    public NetworkStatus NetworkStatus { get; private set; } = NetworkStatus.Unknown;

    public event Action<NetworkStatus>? NetworkStatusChanged;

    public VpnState VpnState => _tunnel?.State ?? VpnState.Disconnected;

    public event Action<VpnState>? VpnStateChanged;

    public event Action<PeerInfo>? PeerConnected;
    public event Action<PeerInfo>? PeerDisconnected;

    public event Action<DirectChat>? IncomingDirectChat;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _mux.Start();
        var peerTopic = Topics.ForPeer(Id);
        foreach (var d in _discoveries)
            _ = RunLoopAsync(() => d.AnnounceAsync(peerTopic, _cts.Token), $"peer announce ({d.GetType().Name})");
        if (_options.Vpn != null || TraversalEnabled)
            _ = Task.Run(() => StartNetworkAsync(_cts.Token));
    }

    private bool TraversalEnabled => _options.Traversal.Enabled && _options.DiscoveryFactory == null;

    private async Task StartNetworkAsync(CancellationToken ct)
    {
        if (_options.Vpn != null)
        {
            try { await ConnectVpnCoreAsync(_options.Vpn, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                _options.Log?.Invoke($"VPN could not start: {ex.Message}");
            }
        }
        await RefreshNetworkAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> ConnectVpnAsync(WireGuardConfig config, CancellationToken ct = default)
    {
        Start();
        bool connected = await ConnectVpnCoreAsync(config, ct).ConfigureAwait(false);
        await RefreshNetworkAsync(ct).ConfigureAwait(false);
        return connected;
    }

    public async Task DisconnectVpnAsync(CancellationToken ct = default)
    {
        DetachTunnel();
        await RefreshNetworkAsync(ct).ConfigureAwait(false);
    }

    private void DetachTunnel()
    {
        var tunnel = Interlocked.Exchange(ref _tunnel, null);
        if (tunnel == null) return;
        _mux.Tunnel = null;
        tunnel.Dispose();
        VpnStateChanged?.Invoke(VpnState.Disconnected);
    }

    private async Task<bool> ConnectVpnCoreAsync(WireGuardConfig config, CancellationToken ct)
    {
        DetachTunnel();
        var tunnel = new WireGuardTunnel(config, _mux, _options.Log);
        tunnel.StateChanged += state => { if (ReferenceEquals(_tunnel, tunnel)) VpnStateChanged?.Invoke(state); };
        _tunnel = tunnel;
        _mux.Tunnel = tunnel;
        try
        {
            bool connected = await tunnel.ConnectAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            _options.Log?.Invoke(connected ? $"VPN connected to {config.Endpoint}" : $"VPN handshake with {config.Endpoint} timed out; still trying");
            return connected;
        }
        catch
        {
            DetachTunnel();
            throw;
        }
    }

    public TurnOptions? Turn => _turnOptions;

    public async Task ConfigureTurnAsync(TurnOptions? turn, CancellationToken ct = default)
    {
        _turnOptions = turn;
        Start();
        await RefreshNetworkAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshNetworkAsync(CancellationToken ct)
    {
        if (!TraversalEnabled) return;
        await _networkGate.WaitAsync(ct).ConfigureAwait(false);
        try { await SetupTraversalAsync(ct).ConfigureAwait(false); }
        finally { _networkGate.Release(); }
    }

    private async Task SetupTraversalAsync(CancellationToken ct)
    {
        try
        {
            var traversal = _options.Traversal;
            var turnOptions = _turnOptions;
            PortMapping? mapping = null;
            _turn?.Dispose();
            _turn = null;
            string? vpn = _tunnel is { State: VpnState.Connected } ? $"VPN {_tunnel.Describe()}" : null;
            if (traversal.PortMapping && _tunnel == null)
            {
                _mapper = new PortMapper(_mux.LocalPort, log: _options.Log);
                mapping = await _mapper.MapAsync(ct).ConfigureAwait(false);
                if (mapping != null) _mux.AdvertisedPort = mapping.ExternalEndPoint.Port;
            }

            var nat = await NatProbe.RunAsync(_mux, traversal.StunServers, ct).ConfigureAwait(false);
            PublishNetwork(new NetworkStatus(nat, mapping, null, vpn));

            bool needRelay = mapping == null && nat.Type is NatType.Symmetric or NatType.Unknown;
            if (turnOptions is { Enabled: true } turn && (needRelay || turn.Always))
            {
                _turn = new TurnClient(_mux, turn, _options.Log);
                if (await _turn.AllocateAsync(ct).ConfigureAwait(false))
                    PublishNetwork(new NetworkStatus(nat, mapping, _turn.RelayedEndPoint, vpn));
            }
            else if (needRelay)
            {
                _options.Log?.Invoke("TURN relay is off; connections from some networks may fail");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _options.Log?.Invoke($"network setup failed: {ex.Message}");
        }
    }

    private void PublishNetwork(NetworkStatus status)
    {
        NetworkStatus = status;
        _options.Log?.Invoke($"network: {status.Describe()}");
        NetworkStatusChanged?.Invoke(status);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var room in _rooms.Values.ToList()) LeaveRoomCore(room);
        Network.Dispose();
        _turn?.Dispose();
        _mux.Tunnel = null;
        _tunnel?.Dispose();
        if (_mapper != null)
        {
            try { _mapper.UnmapAsync().Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
            _mapper.Dispose();
        }
        foreach (var d in _discoveries) d.Dispose();
        _mux.Dispose();
        if (_ownsIdentity) _identity.Dispose();
    }

    public Task<ChatRoom> JoinRoomAsync(string name, CancellationToken ct = default) =>
        JoinCoreAsync(new ChatRoom(this, name, secret: null), ct);

    public Task<ChatRoom> JoinPrivateRoomAsync(RoomKey key, string label = "private", CancellationToken ct = default) =>
        JoinCoreAsync(new ChatRoom(this, label, key), ct);

    private async Task<ChatRoom> JoinCoreAsync(ChatRoom room, CancellationToken ct)
    {
        Start();
        room.History = _histories.GetOrAdd(room.Key, k =>
            new RoomHistory(k, _options.HistoryStore, _options.MaxHistoryMessages, _options.MaxHistoryAge));
        if (!_rooms.TryAdd(room.Key, room)) return _rooms[room.Key];

        foreach (var peer in _peers.Values)
        {
            Send(peer.Connection, new Wire { Type = Wire.Join, Room = RefTo(room, peer) });
            if (peer.Declares(RefFrom(room, peer))) OnPeerEntersRoom(peer, room);
        }

        _ = RunLoopAsync(() => DiscoverRoomAsync(room, _cts.Token), $"room {room.Name}", () => !_rooms.ContainsKey(room.Key));
        _ = RunLoopAsync(() => MaintainRoomAsync(room), $"maintain {room.Name}", () => !_rooms.ContainsKey(room.Key), _options.MaintenanceInterval);
        return room;
    }

    private string RefTo(ChatRoom room, PeerState peer) => room.RefFor(peer.Connection.SessionId!, Id);
    private static string RefFrom(ChatRoom room, PeerState peer) => room.RefFor(peer.Connection.SessionId!, peer.Id);

    private ChatRoom? ResolveRoom(PeerState peer, string reference) =>
        _rooms.Values.FirstOrDefault(r => RefFrom(r, peer) == reference);

    internal Task LeaveRoomAsync(ChatRoom room)
    {
        LeaveRoomCore(room);
        return Task.CompletedTask;
    }

    private void LeaveRoomCore(ChatRoom room)
    {
        if (!_rooms.TryRemove(room.Key, out _)) return;
        var gone = SignedPresence.Create(_identity, room.Key, _options.DisplayName, NextTimestamp(), gone: true);
        foreach (var peer in _peers.Values)
        {
            if (room.MemberStates.ContainsKey(peer.Id))
                Send(peer.Connection, new Wire { Type = Wire.Presence, Room = RefTo(room, peer), Pres = new[] { gone } });
            Send(peer.Connection, new Wire { Type = Wire.Leave, Room = RefTo(room, peer) });
        }
    }

    private async Task DiscoverRoomAsync(ChatRoom room, CancellationToken ct)
    {
        await Task.WhenAll(_discoveries.Select(async d =>
        {
            try
            {
                await d.AnnounceAsync(room.Topic, ct).ConfigureAwait(false);
                var found = await d.FindPeersAsync(room.Topic, ct).ConfigureAwait(false);
                _options.Log?.Invoke($"room '{room.Name}' via {d.GetType().Name}: {found.Count} endpoint(s){(found.Count > 0 ? ": " + string.Join(", ", found) : "")}");
                if (_options.TargetConnections <= 0)
                {
                    foreach (var ep in found) Network.Connect(ep);
                    return;
                }
                foreach (var ep in found) AddCandidate(ep, null, null);
                TopUpConnections(room);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _options.Log?.Invoke($"{d.GetType().Name} failed for room '{room.Name}': {ex.Message}");
            }
        })).ConfigureAwait(false);
    }

    internal void SendRoomMessage(ChatRoom room, string text)
    {
        var message = SignedMessage.Create(_identity, room.Key, _options.DisplayName, text, NextTimestamp());
        room.History.TryAdd(message);
        _options.Log?.Invoke($"sending a message in '{room.Name}' to {room.MemberStates.Count} member(s)");
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Message, Room = RefTo(room, member), Msgs = new[] { message } });
    }

    private long _lastTimestamp;

    private long NextTimestamp()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (true)
        {
            long last = Interlocked.Read(ref _lastTimestamp);
            long next = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref _lastTimestamp, next, last) == last) return next;
        }
    }

    internal IReadOnlyList<ChatMessage> GetHistory(ChatRoom room) =>
        room.History.Snapshot().Select(m => ToChatMessage(room, m, catchUp: false)).ToList();

    private ChatMessage ToChatMessage(ChatRoom room, StoredMessage m, bool catchUp)
    {
        var id = new PeerId(m.From);
        var info = _peers.TryGetValue(id, out var p)
            ? p.Info with { DisplayName = m.Name }
            : new PeerInfo(id, m.Name, null);
        return new ChatMessage(info, room.Name, m.Id, m.Text, DateTimeOffset.FromUnixTimeMilliseconds(m.Ts), catchUp);
    }

    private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(2);

    private void OnGroupMessage(PeerState peer, ChatRoom room, StoredMessage[] messages, bool live)
    {
        foreach (var m in messages.Take(live ? 1 : 100))
        {
            if (!IsValidPeerId(m.From)) continue;
            if (m.From == Id.Value) continue;
            if (!SignedMessage.Verify(m, room.Key))
            {
                _options.Log?.Invoke($"dropped a message from {new PeerId(m.From).Short} in '{room.Name}': bad signature, or its clock is more than 5 minutes ahead of ours");
                continue;
            }
            bool fresh = live && m.Ts >= DateTimeOffset.UtcNow.Subtract(LiveWindow).ToUnixTimeMilliseconds();
            if (!room.History.TryAdd(m)) continue;
            _options.Log?.Invoke($"message from {new PeerId(m.From).Short} in '{room.Name}' via {peer.Id.Short}{(fresh ? "" : " (catch-up)")}");
            room.RaiseMessage(ToChatMessage(room, m, catchUp: !fresh));
            if (fresh) ForwardToMembers(peer, room, m);
        }
    }

    private void ForwardToMembers(PeerState from, ChatRoom room, StoredMessage m) =>
        Relay(from, room, m.From, reference => new Wire { Type = Wire.Message, Room = reference, Msgs = new[] { m } });

    private void Relay(PeerState from, ChatRoom room, string authorId, Func<string, Wire> build)
    {
        var skip = from.Links(room.Key);
        if (room.MemberStates.TryGetValue(new PeerId(authorId), out var author))
            foreach (var kv in author.Links(room.Key)) skip[kv.Key] = kv.Value;

        foreach (var member in room.MemberStates.Values)
        {
            if (member.Id == from.Id || member.Id.Value == authorId || skip.ContainsKey(member.Id.Value)) continue;
            Send(member.Connection, build(RefTo(room, member)));
        }
    }

    private void SendLinks(ChatRoom room)
    {
        var direct = room.MemberStates.Values
            .Select(s => new WirePeer { Id = s.Id.Value, Name = s.Name })
            .ToArray();
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire
            {
                Type = Wire.Links, Room = RefTo(room, member), Deg = _peers.Count,
                Peers = direct.Where(p => p.Id != member.Id.Value).ToArray(),
            });
    }

    private TimeSpan PresenceLifetime => _options.PresenceInterval * 3;

    private void SendPresence(ChatRoom room)
    {
        var beacon = SignedPresence.Create(_identity, room.Key, _options.DisplayName, NextTimestamp(), gone: false);
        foreach (var member in room.MemberStates.Values)
            Send(member.Connection, new Wire { Type = Wire.Presence, Room = RefTo(room, member), Pres = new[] { beacon } });
    }

    private void SendPresenceTo(ChatRoom room, PeerState peer, bool includeCache)
    {
        var beacons = new List<StoredPresence> { SignedPresence.Create(_identity, room.Key, _options.DisplayName, NextTimestamp(), gone: false) };
        if (includeCache)
        {
            long cutoff = Environment.TickCount64 - (long)PresenceLifetime.TotalMilliseconds;
            lock (room.IndirectGate)
                beacons.AddRange(room.Presence.Values.Where(e => !e.Beacon.Gone && e.SeenAt >= cutoff && e.Beacon.From != peer.Id.Value)
                    .Take(63).Select(e => e.Beacon));
        }
        Send(peer.Connection, new Wire { Type = Wire.Presence, Room = RefTo(room, peer), Pres = beacons.ToArray() });
    }

    private void OnPresence(PeerState peer, ChatRoom room, StoredPresence[] beacons)
    {
        bool changed = false;
        foreach (var b in beacons.Take(64))
        {
            if (!IsValidPeerId(b.From) || b.From == Id.Value || !SignedPresence.Verify(b, room.Key)) continue;
            var id = new PeerId(b.From);
            lock (room.IndirectGate)
            {
                if (room.Presence.TryGetValue(id, out var old) && old.Beacon.Ts >= b.Ts) continue;
                if (room.Presence.Count >= 1000 && !room.Presence.ContainsKey(id)) continue;
                room.Presence[id] = new PresenceEntry(b, Environment.TickCount64);
            }
            changed = true;
            Relay(peer, room, b.From, reference => new Wire { Type = Wire.Presence, Room = reference, Pres = new[] { b } });
        }
        if (changed) RefreshIndirect(room);
    }

    private void RefreshIndirect(ChatRoom room)
    {
        var joined = new List<PeerInfo>();
        var left = new List<PeerInfo>();
        lock (room.IndirectGate)
        {
            var reachable = new Dictionary<PeerId, string>();
            foreach (var member in room.MemberStates.Values)
                foreach (var kv in member.Links(room.Key))
                {
                    var id = new PeerId(kv.Key);
                    if (id != Id && !room.MemberStates.ContainsKey(id)) reachable.TryAdd(id, kv.Value);
                }

            long cutoff = Environment.TickCount64 - (long)PresenceLifetime.TotalMilliseconds;
            foreach (var (id, entry) in room.Presence)
                if (!entry.Beacon.Gone && entry.SeenAt >= cutoff && id != Id && !room.MemberStates.ContainsKey(id))
                    reachable.TryAdd(id, entry.Beacon.Name);

            foreach (var id in room.Indirect.Keys.ToList())
                if (!reachable.ContainsKey(id) && room.Indirect.TryRemove(id, out var gone) && !room.MemberStates.ContainsKey(id)) left.Add(gone);
            foreach (var (id, name) in reachable)
            {
                var info = new PeerInfo(id, name, null, IsRelayed: true);
                if (room.Indirect.TryAdd(id, info)) joined.Add(info);
                else room.Indirect[id] = info;
            }
        }
        foreach (var p in left) room.RaiseLeft(p);
        foreach (var p in joined) room.RaiseJoined(p);
    }

    private void AfterMembershipChange(ChatRoom room)
    {
        SendLinks(room);
        RefreshIndirect(room);
    }

    private void OnSyncRequest(PeerState peer, ChatRoom room, string[] have)
    {
        if (!_options.SyncHistory || !peer.TryStartServing(room.Key)) return;
        var missing = room.History.Missing(new HashSet<string>(have), cap: 500);
        for (int i = 0; i < missing.Count; i += 20)
            Send(peer.Connection, new Wire { Type = Wire.History, Room = RefTo(room, peer), Msgs = missing.Skip(i).Take(20).ToArray() });
    }

    private void OnPeerEntersRoom(PeerState peer, ChatRoom room)
    {
        if (!room.MemberStates.TryAdd(peer.Id, peer)) return;
        _options.Log?.Invoke($"{peer.Id.Short} entered room '{room.Name}'");

        var others = room.MemberStates.Values.Where(m => m.Id != peer.Id && !m.Connection.IsRelayed).ToList();
        if (_options.TargetConnections > 0)
        {
            others = others.Where(o => o.Degree < _options.TargetConnections).OrderBy(_ => Random.Shared.Next())
                .Take(Math.Max(1, _options.IntroductionsPerJoin)).ToList();
            if (peer.Degree >= _options.TargetConnections) others.Clear();
        }
        if (others.Count > 0 && !peer.Connection.IsRelayed)
        {
            Send(peer.Connection, new Wire { Type = Wire.Introduce, Room = RefTo(room, peer), Peers = others.Select(ToWirePeer).ToArray() });
            foreach (var other in others)
                Send(other.Connection, new Wire { Type = Wire.Introduce, Room = RefTo(room, other), Peers = new[] { ToWirePeer(peer) } });
        }
        bool wasIndirect = room.Indirect.TryRemove(peer.Id, out _);
        if (!wasIndirect) room.RaiseJoined(peer.Info);
        AfterMembershipChange(room);
        SendPresenceTo(room, peer, includeCache: true);

        if (_options.SyncHistory)
        {
            peer.HistoryExpectedUntil = Environment.TickCount64 + 60_000;
            Send(peer.Connection, new Wire { Type = Wire.Sync, Room = RefTo(room, peer), Have = room.History.Keys() });
        }
    }

    private static WirePeer ToWirePeer(PeerState p) => new() { Id = p.Id.Value, EndPoint = p.Connection.RemoteEndPoint.ToString() };

    public async Task<DirectChat> ConnectAsync(IPEndPoint endPoint, PeerId? expected = null, CancellationToken ct = default)
    {
        Start();
        _pinned[endPoint.ToString()] = 0;
        if (expected is { } pin) _pinned[pin.Value] = 0;
        var conn = Network.Connect(endPoint, expected);
        await conn.WhenEstablished.WaitAsync(ct).ConfigureAwait(false);
        return GetDirectChat(conn.RemoteId!.Value);
    }

    public async Task<DirectChat> OpenDirectChatAsync(PeerId peer, CancellationToken ct = default)
    {
        Start();
        _pinned[peer.Value] = 0;
        var topic = Topics.ForPeer(peer);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (_peers.ContainsKey(peer)) return GetDirectChat(peer);

            var attempts = (await Discovery.FindPeersAsync(topic, ct).ConfigureAwait(false))
                .Select(ep => Network.Connect(ep, peer).WhenEstablished)
                .Cast<Task>()
                .ToList();
            attempts.Add(Task.Delay(TimeSpan.FromSeconds(3), ct));
            await Task.WhenAny(attempts).ConfigureAwait(false);
        }
    }

    public DirectChat GetDirectChat(PeerId peer) => _directChats.GetOrAdd(peer, id => new DirectChat(this, id));

    internal PeerInfo? GetPeerInfo(PeerId id) => _peers.TryGetValue(id, out var p) ? p.Info : null;

    internal void SendDirectMessage(PeerId peer, string text)
    {
        if (!_peers.TryGetValue(peer, out var state)) throw new InvalidOperationException($"Not connected to {peer.Short}.");
        Send(state.Connection, NewMessage(text, room: null));
    }

    private void OnConnectionEstablished(PeerConnection conn)
    {
        var state = new PeerState(conn);
        bool isNew = !_peers.ContainsKey(state.Id);
        _peers[state.Id] = state;
        foreach (var room in _rooms.Values)
            if (room.MemberStates.ContainsKey(state.Id)) room.MemberStates[state.Id] = state;
        Send(conn, new Wire { Type = Wire.Hello, Name = _options.DisplayName, Deg = _peers.Count, Rooms = _rooms.Values.Select(r => RefTo(r, state)).ToArray() });
        if (isNew) _options.Log?.Invoke($"connected to {state.Id.Short} at {conn.RemoteEndPoint}");
    }

    private void OnConnectionClosed(PeerConnection conn)
    {
        var id = conn.RemoteId!.Value;
        if (!_peers.TryGetValue(id, out var state) || !ReferenceEquals(state.Connection, conn)) return;
        if (!_peers.TryRemove(new KeyValuePair<PeerId, PeerState>(id, state))) return;
        _options.Log?.Invoke($"disconnected from {id.Short} at {conn.RemoteEndPoint}: connection {conn.CloseReason ?? "closed"}");

        foreach (var room in _rooms.Values)
        {
            if (!room.MemberStates.TryRemove(id, out var gone)) continue;
            string? name = null;
            lock (room.IndirectGate)
            {
                foreach (var other in room.MemberStates.Values)
                    if (other.Links(room.Key).TryGetValue(id.Value, out name)) break;
                if (name == null && room.Presence.TryGetValue(id, out var entry) && !entry.Beacon.Gone
                    && entry.SeenAt >= Environment.TickCount64 - (long)PresenceLifetime.TotalMilliseconds)
                    name = entry.Beacon.Name;
                if (name != null) room.Indirect[id] = gone.Info with { EndPoint = null, IsRelayed = true };
            }
            if (name == null) room.RaiseLeft(gone.Info);
            AfterMembershipChange(room);
        }
        PeerDisconnected?.Invoke(state.Info);
        if (_introducedVia.ContainsKey(id)) ScheduleRelayFallback(id);
    }

    private void OnChatData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (!reliable || conn.RemoteId is not { } id) return;
        if (!_peers.TryGetValue(id, out var peer))
        {
            _options.Log?.Invoke($"chat data from {id.Short} arrived before the connection was registered");
            return;
        }

        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(data.Span, Json); }
        catch (JsonException ex)
        {
            _options.Log?.Invoke($"unreadable chat data from {id.Short}: {ex.Message}");
            return;
        }
        if (wire == null) return;

        switch (wire.Type)
        {
            case Wire.Hello:
                bool first = peer.Name == null;
                peer.Name = string.IsNullOrWhiteSpace(wire.Name) ? peer.Id.Short : wire.Name;
                peer.SetRooms(wire.Rooms ?? Array.Empty<string>());
                peer.Degree = wire.Deg;
                _options.Log?.Invoke($"hello from {peer.Id.Short} ({peer.Name}): declares {wire.Rooms?.Length ?? 0} room(s); we are in {_rooms.Count}");
                if (first && !EnforceCap(peer)) break;
                if (first) PeerConnected?.Invoke(peer.Info);
                foreach (var room in _rooms.Values)
                    if (peer.Declares(RefFrom(room, peer))) OnPeerEntersRoom(peer, room);
                break;

            case Wire.Join when wire.Room != null:
                peer.AddRoom(wire.Room);
                if (ResolveRoom(peer, wire.Room) is { } joined) OnPeerEntersRoom(peer, joined);
                break;

            case Wire.Leave when wire.Room != null:
                peer.RemoveRoom(wire.Room);
                if (ResolveRoom(peer, wire.Room) is { } left && left.MemberStates.TryRemove(peer.Id, out _))
                {
                    peer.ClearLinks(left.Key);
                    left.RaiseLeft(peer.Info);
                    AfterMembershipChange(left);
                }
                break;

            case Wire.Links when wire.Room != null && wire.Peers != null
                                 && ResolveRoom(peer, wire.Room) is { } linkRoom && linkRoom.MemberStates.ContainsKey(peer.Id):
                var links = new Dictionary<string, string>();
                foreach (var p in wire.Peers.Take(256))
                    if (IsValidPeerId(p.Id) && p.Id != peer.Id.Value)
                        links[p.Id] = string.IsNullOrWhiteSpace(p.Name) ? new PeerId(p.Id).Short : p.Name.Length > 64 ? p.Name[..64] : p.Name;
                peer.SetLinks(linkRoom.Key, links);
                if (wire.Deg > 0) peer.Degree = wire.Deg;
                RefreshIndirect(linkRoom);
                break;

            case Wire.Presence when wire.Room != null && wire.Pres != null
                                    && ResolveRoom(peer, wire.Room) is { } presRoom && presRoom.MemberStates.ContainsKey(peer.Id):
                OnPresence(peer, presRoom, wire.Pres);
                break;

            case Wire.Message when wire.Room == null && wire.Text != null && wire.Id != null:
                var msg = new ChatMessage(peer.Info, null, wire.Id, wire.Text, DateTimeOffset.FromUnixTimeMilliseconds(wire.Ts));
                bool existed = _directChats.ContainsKey(peer.Id);
                var chat = GetDirectChat(peer.Id);
                if (!existed) IncomingDirectChat?.Invoke(chat);
                chat.RaiseMessage(msg);
                break;

            case Wire.Message when wire.Room != null && wire.Msgs != null
                                   && ResolveRoom(peer, wire.Room) is { } liveRoom && liveRoom.MemberStates.ContainsKey(peer.Id):
                OnGroupMessage(peer, liveRoom, wire.Msgs, live: true);
                break;

            case Wire.Sync when wire.Room != null && wire.Have != null
                                && ResolveRoom(peer, wire.Room) is { } syncRoom && syncRoom.MemberStates.ContainsKey(peer.Id):
                OnSyncRequest(peer, syncRoom, wire.Have.Take(2000).ToArray());
                break;

            case Wire.History when wire.Room != null && wire.Msgs != null && Environment.TickCount64 < peer.HistoryExpectedUntil
                                   && ResolveRoom(peer, wire.Room) is { } histRoom && histRoom.MemberStates.ContainsKey(peer.Id):
                OnGroupMessage(peer, histRoom, wire.Msgs, live: false);
                break;

            case Wire.Introduce when wire.Room != null && wire.Peers != null
                                     && ResolveRoom(peer, wire.Room) is { } introRoom && introRoom.MemberStates.ContainsKey(peer.Id):
                foreach (var p in wire.Peers.Take(64))
                {
                    if (!IsValidPeerId(p.Id) || p.Id == Id.Value) continue;
                    var target = new PeerId(p.Id);
                    if (_peers.ContainsKey(target) || peer.Connection.IsRelayed) continue;
                    if (!IPEndPoint.TryParse(p.EndPoint, out var ep)) continue;

                    if (_options.TargetConnections <= 0)
                    {
                        _introducedVia.GetOrAdd(target, _ => new()).TryAdd(peer.Id, 0);
                        Network.Connect(ep, target);
                        ScheduleRelayFallback(target);
                        continue;
                    }
                    var candidate = AddCandidate(ep, target, peer.Id);
                    if (WantsMore(introRoom) && CanDial(candidate)) Dial(candidate);
                }
                break;
        }
    }

    private static bool IsValidPeerId(string id) =>
        id.Length == 64 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private void ScheduleRelayFallback(PeerId target)
    {
        if (!_options.EnableRelaying || !_relayPending.TryAdd(target, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.RelayFallbackDelay, _cts.Token).ConfigureAwait(false);
                while (!_cts.IsCancellationRequested && !_peers.ContainsKey(target) && !IsBackedOff(target.Value, Environment.TickCount64))
                {
                    var relay = _introducedVia.TryGetValue(target, out var vias)
                        ? vias.Keys.Select(r => _peers.GetValueOrDefault(r)?.Connection)
                              .FirstOrDefault(c => c is { IsRelayed: false, State: ConnectionState.Established })
                        : null;
                    if (relay == null) break;

                    _options.Log?.Invoke($"no direct path to {target.Short}; relaying via {relay.RemoteId!.Value.Short}");
                    var conn = Network.ConnectVia(relay, target);
                    await Task.WhenAny(conn.WhenEstablished, Task.Delay(_options.Network.ConnectTimeout, _cts.Token)).ConfigureAwait(false);
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _options.Log?.Invoke($"relay fallback for {target.Short} failed: {ex.Message}"); }
            finally { _relayPending.TryRemove(target, out _); }
        });
    }

    private Wire NewMessage(string text, string? room) => new()
    {
        Type = Wire.Message,
        Room = room,
        Id = Guid.NewGuid().ToString("N"),
        Text = text,
        Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private void Send(PeerConnection conn, Wire wire)
    {
        try { conn.SendReliable(ChannelIds.Chat, JsonSerializer.SerializeToUtf8Bytes(wire, Json)); }
        catch (InvalidOperationException) {  }
    }

    private bool WantsMore(ChatRoom room)
    {
        int target = _options.TargetConnections;
        if (target <= 0) return true;
        if (room.MemberStates.Count >= target) return false;
        return _options.MaxConnections <= 0 || _peers.Count < _options.MaxConnections;
    }

    private Candidate AddCandidate(IPEndPoint endPoint, PeerId? id, PeerId? via)
    {
        var candidate = _candidates.GetOrAdd(endPoint.ToString(), _ => new Candidate(endPoint));
        if (id != null) candidate.Id = id;
        if (via != null) candidate.Via = via;
        candidate.Seen = Environment.TickCount64;
        if (_candidates.Count > MaxCandidates)
            foreach (var key in _candidates.OrderBy(kv => kv.Value.Seen).Take(_candidates.Count - MaxCandidates).Select(kv => kv.Key).ToList())
                _candidates.TryRemove(key, out _);
        return candidate;
    }

    private bool IsBackedOff(string key, long now) => _backoff.TryGetValue(key, out var until) && now < until;

    private bool IsConnectedTo(IPEndPoint endPoint) => _peers.Values.Any(p => p.Connection.RemoteEndPoint.Equals(endPoint));

    private bool IsDialable(Candidate c) =>
        (c.LastDial == 0 || Environment.TickCount64 - c.LastDial >= RedialMs) && CanDial(c);

    private bool CanDial(Candidate c)
    {
        long now = Environment.TickCount64;
        if (c.EndPoint.Port == _mux.LocalPort && IPAddress.IsLoopback(c.EndPoint.Address)) return false;
        if (IsBackedOff(c.EndPoint.ToString(), now)) return false;
        if (c.Id is { } id && (IsBackedOff(id.Value, now) || _peers.ContainsKey(id))) return false;
        return !IsConnectedTo(c.EndPoint);
    }

    private void Dial(Candidate c)
    {
        c.LastDial = Environment.TickCount64;
        if (c.Id is { } id && c.Via is { } via)
        {
            _introducedVia.GetOrAdd(id, _ => new()).TryAdd(via, 0);
            Network.Connect(c.EndPoint, id);
            ScheduleRelayFallback(id);
        }
        else Network.Connect(c.EndPoint, c.Id);
    }

    private void TopUpConnections(ChatRoom room)
    {
        if (_options.TargetConnections <= 0) return;
        long now = Environment.TickCount64;
        int pending = _candidates.Values.Count(c => c.LastDial != 0 && now - c.LastDial < RedialMs && !IsConnectedTo(c.EndPoint));
        int need = _options.TargetConnections - room.MemberStates.Count - pending;
        if (_options.MaxConnections > 0) need = Math.Min(need, _options.MaxConnections - _peers.Count - pending);
        if (need <= 0) return;
        foreach (var c in _candidates.Values.Where(IsDialable).OrderBy(_ => Random.Shared.Next()).Take(need).ToList()) Dial(c);
    }

    private bool IsPinned(PeerState p) =>
        _pinned.ContainsKey(p.Id.Value) || _pinned.ContainsKey(p.Connection.RemoteEndPoint.ToString()) || _directChats.ContainsKey(p.Id);

    private bool EnforceCap(PeerState peer)
    {
        int max = _options.MaxConnections;
        if (max <= 0 || _peers.Count <= max || IsPinned(peer)) return true;

        if (peer.Degree <= MinDegree)
        {
            var victims = _peers.Values
                .Where(p => p.Id != peer.Id && p.Name != null && p.Degree > MinDegree && !IsPinned(p))
                .ToList();
            if (victims.Count > 0)
            {
                Drop(victims[Random.Shared.Next(victims.Count)]);
                return true;
            }
        }
        Drop(peer);
        return false;
    }

    private void Drop(PeerState peer)
    {
        long until = Environment.TickCount64 + BackoffMs;
        _backoff[peer.Id.Value] = until;
        _backoff[peer.Connection.RemoteEndPoint.ToString()] = until;
        _options.Log?.Invoke($"over the connection limit; dropping {peer.Id.Short}");
        peer.Connection.Close(notifyPeer: true);
    }

    private Task MaintainRoomAsync(ChatRoom room)
    {
        long now = Environment.TickCount64;
        TopUpConnections(room);

        if (now - room.LastPresenceAt >= _options.PresenceInterval.TotalMilliseconds)
        {
            room.LastPresenceAt = now;
            SendPresence(room);
        }
        if (_options.TargetConnections > 0 && now - room.LastShuffleAt >= _options.ShuffleInterval.TotalMilliseconds)
        {
            room.LastShuffleAt = now;
            ShuffleMembers(room);
        }

        long stale = now - (long)(PresenceLifetime.TotalMilliseconds * 2);
        lock (room.IndirectGate)
            foreach (var id in room.Presence.Where(kv => kv.Value.SeenAt < stale).Select(kv => kv.Key).ToList()) room.Presence.Remove(id);
        RefreshIndirect(room);
        return Task.CompletedTask;
    }

    private void ShuffleMembers(ChatRoom room)
    {
        var members = room.MemberStates.Values.Where(m => !m.Connection.IsRelayed).ToList();
        foreach (var member in members)
        {
            var sample = members.Where(o => o.Id != member.Id).OrderBy(_ => Random.Shared.Next()).Take(3).Select(ToWirePeer).ToArray();
            if (sample.Length > 0)
                Send(member.Connection, new Wire { Type = Wire.Introduce, Room = RefTo(room, member), Peers = sample });
        }
    }

    private async Task RunLoopAsync(Func<Task> action, string what, Func<bool>? stop = null, TimeSpan? every = null)
    {
        try
        {
            while (!_cts.IsCancellationRequested && stop?.Invoke() != true)
            {
                try { await action().ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _options.Log?.Invoke($"{what} failed: {ex.Message}");
                }
                await Task.Delay(every ?? _options.DiscoveryInterval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}
