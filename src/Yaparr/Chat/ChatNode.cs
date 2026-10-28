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

    /// <summary>File where recently seen room members are remembered, so a restart can dial them directly. Null disables it.</summary>
    public string? PeerCachePath { get; init; }

    /// <summary>How long to wait for a recipient to confirm a message before reporting it as not delivered.</summary>
    public TimeSpan AckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an unconfirmed message is kept for re-sending when its recipient reconnects.</summary>
    public TimeSpan ResendWindow { get; init; } = TimeSpan.FromMinutes(10);

    public Action<string>? Log { get; init; }
}

public sealed partial class ChatNode : IDisposable
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
    private readonly ConcurrentDictionary<string, Outgoing> _outbox = new();
    private readonly ConcurrentDictionary<string, long> _seenDirect = new();
    private readonly ConcurrentDictionary<PeerId, Redial> _redial = new();
    private PeerCache _peerCache = null!;
    private bool _started;
    private PortMapper? _mapper;
    private TurnClient? _turn;
    private TurnOptions? _turnOptions;
    private WireGuardTunnel? _tunnel;
    private readonly SemaphoreSlim _networkGate = new(1, 1);

    private const int MinDegree = 2;
    private const int MaxCandidates = 512;

    // Discovery results come from anyone (an address can be announced for a room it does not belong to), so the
    // number of first-contact packets we send is limited: no more than DialBudget new dials per DialWindowMs, with
    // the first DialImmediately dials of a top-up go out at once (small rooms connect without delay) and the rest are spread out by DialStaggerMs.
    private const int DialBudget = 24;
    private const long DialWindowMs = 30_000;
    private const int DialStaggerMs = 250;
    private const int DialImmediately = 3;
    private const int MaxDirectDials = 16;
    private readonly Queue<long> _dialTimes = new();
    private readonly ConcurrentDictionary<string, long> _directDialed = new();
    private static readonly long BackoffMs = 60_000;
    private long RedialMs => (long)Math.Max(500, _options.DiscoveryInterval.TotalMilliseconds);

    private sealed class Redial
    {
        public Redial(IPEndPoint endPoint) => EndPoint = endPoint;
        public IPEndPoint EndPoint { get; }
        public int Attempts;
        public long Next;
    }

    private sealed class Candidate
    {
        public Candidate(IPEndPoint endPoint) => EndPoint = endPoint;
        public IPEndPoint EndPoint { get; }
        public PeerId? Id { get; set; }
        public PeerId? Via { get; set; }
        public long LastDial;
        public long Seen;
        /// <summary>Higher is tried first: addresses that worked before beat addresses from a search.</summary>
        public int Rank;
    }

    public ChatNode(ChatNodeOptions? options = null)
    {
        _options = options ?? new ChatNodeOptions();
        _peerCache = new PeerCache(_options.PeerCachePath);
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
        _ = Task.Run(OutboxLoopAsync);
        _ = Task.Run(RedialLoopAsync);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnAddressChanged;
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
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _peerCache.Save();
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

        // The cache lists the most recently seen first.
        int rank = _peerCache.For(room.Key).Count;
        foreach (var (cachedId, cachedEndPoint) in _peerCache.For(room.Key))
            if (cachedId != Id) AddCandidate(cachedEndPoint, cachedId, null).Rank = Math.Max(1, rank--);

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
                if (found.Any(IsSomeoneElse) && d is not LocalServiceDiscovery) _lastFoundAt = Environment.TickCount64;
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

    internal string SendRoomMessage(ChatRoom room, string text)
    {
        var message = SignedMessage.Create(_identity, room.Key, _options.DisplayName, text, NextTimestamp());
        room.History.TryAdd(message);
        _options.Log?.Invoke($"sending a message in '{room.Name}' to {room.MemberStates.Count} member(s)");
        var members = room.MemberStates.Values.ToList();
        var outgoing = new Outgoing(message.Id) { Room = room, Signed = message, SentAt = Environment.TickCount64 };
        foreach (var member in members) outgoing.Targets.Add(member.Id);
        _outbox[message.Id] = outgoing;
        foreach (var member in members)
            Send(member.Connection, new Wire { Type = Wire.Message, Room = RefTo(room, member), Msgs = new[] { message } });
        PublishDelivery(outgoing);
        return message.Id;
    }

    private sealed class Outgoing
    {
        public Outgoing(string id) => Id = id;
        public string Id { get; }
        public ChatRoom? Room { get; init; }
        public StoredMessage? Signed { get; init; }
        public PeerId? DirectPeer { get; init; }
        public Wire? DirectWire { get; init; }
        public long SentAt;
        public readonly HashSet<PeerId> Targets = new();
        public readonly HashSet<PeerId> Acked = new();
        public DeliveryStatus? Last;
    }

    private void PublishDelivery(Outgoing o)
    {
        DeliveryStatus status;
        lock (o)
        {
            int total = o.Targets.Count;
            int delivered = o.Targets.Count(o.Acked.Contains);
            var state = total == 0 ? DeliveryState.Queued
                : delivered == total ? DeliveryState.Delivered
                : Environment.TickCount64 - o.SentAt < _options.AckTimeout.TotalMilliseconds ? DeliveryState.Sending
                : delivered == 0 ? DeliveryState.NotDelivered : DeliveryState.PartlyDelivered;
            status = new DeliveryStatus(o.Id, state, delivered, total);
            if (status == o.Last) return;
            o.Last = status;
        }
        if (o.Room != null) o.Room.RaiseDelivery(status);
        else if (o.DirectPeer is { } peer && _directChats.TryGetValue(peer, out var chat)) chat.RaiseDelivery(status);
    }

    private int _networkKickPending;

    private void OnAddressChanged(object? sender, EventArgs e) => NotifyNetworkChanged();

    /// <summary>
    /// Call when the machine's network changed (Wi-Fi to cable, resume from sleep, VPN toggled outside the app).
    /// The NAT type and port mapping are checked again and dropped peers are redialed soon after.
    /// Several calls close together become one check.
    /// </summary>
    public void NotifyNetworkChanged()
    {
        if (_cts.IsCancellationRequested || Interlocked.Exchange(ref _networkKickPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, _cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _networkKickPending, 0);
                _options.Log?.Invoke("network changed; checking the connection again");
                // Sessions on the old network are going to fail; make sure they are dialed again as soon as they drop.
                _backoff.Clear();
                await RefreshNetworkAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally { Volatile.Write(ref _networkKickPending, 0); }
        });
    }

    private const int MaxRedialAttempts = 12;

    // A peer that was connected and dropped is tried again quickly (1s, 2s, 4s ... up to 30s)
    // instead of waiting for the next discovery round.
    private async Task RedialLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                long now = Environment.TickCount64;
                foreach (var (id, r) in _redial)
                {
                    if (_peers.ContainsKey(id) || r.Attempts >= MaxRedialAttempts || IsBackedOff(id.Value, now))
                    {
                        _redial.TryRemove(id, out _);
                        continue;
                    }
                    if (now < r.Next) continue;
                    r.Attempts++;
                    r.Next = now + Math.Min(30_000, 1000L << Math.Min(r.Attempts, 5));
                    _options.Log?.Invoke($"reconnecting to {id.Short} at {r.EndPoint} (attempt {r.Attempts})");
                    Network.Connect(r.EndPoint, id);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task OutboxLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                long now = Environment.TickCount64;
                foreach (var o in _outbox.Values)
                {
                    PublishDelivery(o);
                    if (now - o.SentAt > _options.ResendWindow.TotalMilliseconds) _outbox.TryRemove(o.Id, out _);
                }
                _peerCache.Save();
                long cutoff = now - 600_000;
                foreach (var kv in _seenDirect.Where(kv => kv.Value < cutoff).ToList()) _seenDirect.TryRemove(kv.Key, out _);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ResendTo(PeerState peer, Func<Outgoing, bool> applies, Func<Outgoing, Wire> build)
    {
        foreach (var o in _outbox.Values)
        {
            if (!applies(o)) continue;
            lock (o)
            {
                // Only people who were meant to get it. Newcomers learn older messages through history sync, if enabled.
                if (o.Last?.State == DeliveryState.Delivered || o.Acked.Contains(peer.Id) || !o.Targets.Contains(peer.Id)) continue;
            }
            Send(peer.Connection, build(o));
            PublishDelivery(o);
        }
    }

    internal void RetryMessage(string messageId)
    {
        if (!_outbox.TryGetValue(messageId, out var o)) return;
        List<PeerId> pending;
        lock (o)
        {
            o.SentAt = Environment.TickCount64;
            pending = o.Targets.Where(t => !o.Acked.Contains(t)).ToList();
        }
        foreach (var id in pending)
            if (_peers.TryGetValue(id, out var peer))
                Send(peer.Connection, o.Room != null
                    ? new Wire { Type = Wire.Message, Room = RefTo(o.Room, peer), Msgs = new[] { o.Signed! } }
                    : o.DirectWire!);
        PublishDelivery(o);
    }

    private void OnAck(PeerState peer, Wire wire)
    {
        if (wire.Have == null) return;
        ChatRoom? room = null;
        if (wire.Room != null && (room = ResolveRoom(peer, wire.Room)) == null) return;
        foreach (var id in wire.Have.Take(200))
        {
            if (!_outbox.TryGetValue(id, out var o)) continue;
            if (room != null ? o.Room != room : o.DirectPeer != peer.Id) continue;
            lock (o) { o.Targets.Add(peer.Id); o.Acked.Add(peer.Id); }
            PublishDelivery(o);
        }
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
        var received = new List<string>();
        try { ProcessGroupMessages(peer, room, messages, live, received); }
        finally
        {
            if (received.Count > 0)
                Send(peer.Connection, new Wire { Type = Wire.Ack, Room = RefTo(room, peer), Have = received.ToArray() });
        }
    }

    private void ProcessGroupMessages(PeerState peer, ChatRoom room, StoredMessage[] messages, bool live, List<string> received)
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
            received.Add(m.Id);
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
        if (!peer.Connection.IsRelayed) _peerCache.Remember(room.Key, peer.Id, peer.Connection.RemoteEndPoint);

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
        ResendTo(peer, o => o.Room == room, o => new Wire { Type = Wire.Message, Room = RefTo(room, peer), Msgs = new[] { o.Signed! } });

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
        // The connection is up a moment before the chat layer has registered it; wait so sending works at once.
        var id = conn.RemoteId!.Value;
        for (int i = 0; i < 100 && !_peers.ContainsKey(id); i++) await Task.Delay(20, ct).ConfigureAwait(false);
        return GetDirectChat(id);
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

            // A search can return hundreds of addresses; try a limited number, skipping ones tried a moment ago.
            long now = Environment.TickCount64;
            var attempts = (await Discovery.FindPeersAsync(topic, ct).ConfigureAwait(false))
                .Where(IsSomeoneElse)
                .Where(ep => now - _directDialed.GetValueOrDefault(ep.ToString()) > 10_000)
                .OrderBy(_ => Random.Shared.Next())
                .Take(MaxDirectDials)
                .Select(ep => { _directDialed[ep.ToString()] = now; return Network.Connect(ep, peer).WhenEstablished; })
                .Cast<Task>()
                .ToList();
            attempts.Add(Task.Delay(TimeSpan.FromSeconds(3), ct));
            await Task.WhenAny(attempts).ConfigureAwait(false);
        }
    }

    public DirectChat GetDirectChat(PeerId peer) => _directChats.GetOrAdd(peer, id => new DirectChat(this, id));

    internal PeerInfo? GetPeerInfo(PeerId id) => _peers.TryGetValue(id, out var p) ? p.Info : null;

    internal string SendDirectMessage(PeerId peer, string text)
    {
        if (!_peers.TryGetValue(peer, out var state)) throw new InvalidOperationException($"Not connected to {peer.Short}.");
        var wire = NewMessage(text, room: null);
        var outgoing = new Outgoing(wire.Id!) { DirectPeer = peer, DirectWire = wire, SentAt = Environment.TickCount64 };
        outgoing.Targets.Add(peer);
        _outbox[wire.Id!] = outgoing;
        Send(state.Connection, wire);
        PublishDelivery(outgoing);
        return wire.Id!;
    }

    private void OnConnectionEstablished(PeerConnection conn)
    {
        var state = new PeerState(conn);
        bool isNew = !_peers.ContainsKey(state.Id);
        // The hello must be the first thing on the connection, so send it before anyone can find the state and write to it.
        Send(conn, new Wire { Type = Wire.Hello, Name = _options.DisplayName, Deg = _peers.Count + (isNew ? 1 : 0), Rooms = _rooms.Values.Select(r => RefTo(r, state)).ToArray() });
        _peers[state.Id] = state;
        foreach (var room in _rooms.Values)
            if (room.MemberStates.ContainsKey(state.Id)) room.MemberStates[state.Id] = state;
        ResendTo(state, o => o.DirectPeer == state.Id, o => o.DirectWire!);
        _options.Log?.Invoke(isNew
            ? $"connected to {state.Id.Short} at {conn.RemoteEndPoint}"
            : $"switched to a new connection to {state.Id.Short} at {conn.RemoteEndPoint}");
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
        if (!conn.IsRelayed && !IsBackedOff(id.Value, Environment.TickCount64) && !_cts.IsCancellationRequested)
            _redial[id] = new Redial(conn.RemoteEndPoint) { Next = Environment.TickCount64 + 1000 };
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

            case Wire.Ack:
                OnAck(peer, wire);
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
                Send(conn, new Wire { Type = Wire.Ack, Have = new[] { wire.Id } });
                if (!_seenDirect.TryAdd(peer.Id.Value + wire.Id, Environment.TickCount64)) break;
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

            case Wire.Message when wire.Room != null:
                _options.Log?.Invoke(ResolveRoom(peer, wire.Room) == null
                    ? $"ignored a message from {peer.Id.Short}: it names a room we do not recognise on this connection"
                    : $"ignored a message from {peer.Id.Short}: they are not a member of that room here");
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
        catch (InvalidOperationException ex)
        {
            _options.Log?.Invoke($"could not send to {conn.RemoteId?.Short}: {ex.Message}");
        }
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

    // Discovery also returns our own announcement; that does not count as finding anyone.
    private bool IsSomeoneElse(IPEndPoint endPoint)
    {
        if (endPoint.Port == _mux.LocalPort && IPAddress.IsLoopback(endPoint.Address)) return false;
        var status = NetworkStatus;
        return !endPoint.Equals(status.Nat.PublicEndPoint) && !endPoint.Equals(status.Mapping?.ExternalEndPoint) && !endPoint.Equals(status.Relay);
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

    /// <summary>Takes one dial from the budget; false when too many were made recently.</summary>
    private bool ReserveDial()
    {
        long now = Environment.TickCount64;
        lock (_dialTimes)
        {
            while (_dialTimes.Count > 0 && now - _dialTimes.Peek() > DialWindowMs) _dialTimes.Dequeue();
            if (_dialTimes.Count >= DialBudget) return false;
            _dialTimes.Enqueue(now);
            return true;
        }
    }

    private void Dial(Candidate c)
    {
        if (!ReserveDial()) return;
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
        // Known members first (best remembered first), then the rest in random order so no one address is favoured.
        var wave = _candidates.Values.Where(IsDialable)
            .OrderByDescending(c => c.Rank).ThenByDescending(c => c.Id != null).ThenBy(_ => Random.Shared.Next())
            .Take(need).ToList();
        for (int i = 0; i < wave.Count; i++)
        {
            if (i < DialImmediately) { Dial(wave[i]); continue; }
            var candidate = wave[i];
            int delay = (i - DialImmediately + 1) * DialStaggerMs;
            // Mark it as pending now so the next top-up does not count it twice, then dial if it is still needed.
            candidate.LastDial = Environment.TickCount64;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                    if (WantsMore(room) && CanDial(candidate)) Dial(candidate);
                }
                catch (OperationCanceledException) { }
            });
        }
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
