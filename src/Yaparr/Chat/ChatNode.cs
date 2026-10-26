using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Yaparr.Discovery;
using Yaparr.Discovery.Dht;
using Yaparr.Identity;
using Yaparr.Transport;

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

    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(15);

    public bool EnableRelaying { get; init; } = true;

    public TimeSpan RelayFallbackDelay { get; init; } = TimeSpan.FromSeconds(8);

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
        public PeerId Id => Connection.RemoteId!.Value;
        public PeerInfo Info => new(Id, Name ?? Id.Short, Connection.RemoteEndPoint, Connection.IsRelayed);

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
    private bool _started;

    public ChatNode(ChatNodeOptions? options = null)
    {
        _options = options ?? new ChatNodeOptions();
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
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var room in _rooms.Values.ToList()) LeaveRoomCore(room);
        Network.Dispose();
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
        foreach (var peer in _peers.Values) Send(peer.Connection, new Wire { Type = Wire.Leave, Room = RefTo(room, peer) });
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
                foreach (var ep in found) Network.Connect(ep);
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

    private void OnGroupMessage(PeerState peer, ChatRoom room, StoredMessage[] messages, bool live)
    {
        foreach (var m in messages.Take(live ? 1 : 100))
        {
            if (!IsValidPeerId(m.From) || (live && m.From != peer.Id.Value)) continue;
            if (m.From == Id.Value || !SignedMessage.Verify(m, room.Key)) continue;
            if (room.History.TryAdd(m)) room.RaiseMessage(ToChatMessage(room, m, catchUp: !live));
        }
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
        if (others.Count > 0 && !peer.Connection.IsRelayed)
        {
            Send(peer.Connection, new Wire { Type = Wire.Introduce, Room = RefTo(room, peer), Peers = others.Select(ToWirePeer).ToArray() });
            foreach (var other in others)
                Send(other.Connection, new Wire { Type = Wire.Introduce, Room = RefTo(room, other), Peers = new[] { ToWirePeer(peer) } });
        }
        room.RaiseJoined(peer.Info);

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
        var conn = Network.Connect(endPoint, expected);
        await conn.WhenEstablished.WaitAsync(ct).ConfigureAwait(false);
        return GetDirectChat(conn.RemoteId!.Value);
    }

    public async Task<DirectChat> OpenDirectChatAsync(PeerId peer, CancellationToken ct = default)
    {
        Start();
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
        Send(conn, new Wire { Type = Wire.Hello, Name = _options.DisplayName, Rooms = _rooms.Values.Select(r => RefTo(r, state)).ToArray() });
        if (isNew) _options.Log?.Invoke($"connected to {state.Id.Short} at {conn.RemoteEndPoint}");
    }

    private void OnConnectionClosed(PeerConnection conn)
    {
        var id = conn.RemoteId!.Value;
        if (!_peers.TryGetValue(id, out var state) || !ReferenceEquals(state.Connection, conn)) return;
        if (!_peers.TryRemove(new KeyValuePair<PeerId, PeerState>(id, state))) return;

        foreach (var room in _rooms.Values)
            if (room.MemberStates.TryRemove(id, out var gone)) room.RaiseLeft(gone.Info);
        PeerDisconnected?.Invoke(state.Info);
        if (_introducedVia.ContainsKey(id)) ScheduleRelayFallback(id);
    }

    private void OnChatData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (!reliable || conn.RemoteId is not { } id || !_peers.TryGetValue(id, out var peer)) return;

        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(data.Span, Json); }
        catch (JsonException) { return; }
        if (wire == null) return;

        switch (wire.Type)
        {
            case Wire.Hello:
                bool first = peer.Name == null;
                peer.Name = string.IsNullOrWhiteSpace(wire.Name) ? peer.Id.Short : wire.Name;
                peer.SetRooms(wire.Rooms ?? Array.Empty<string>());
                _options.Log?.Invoke($"hello from {peer.Id.Short} ({peer.Name}): declares {wire.Rooms?.Length ?? 0} room(s); we are in {_rooms.Count}");
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
                    left.RaiseLeft(peer.Info);
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

                    _introducedVia.GetOrAdd(target, _ => new()).TryAdd(peer.Id, 0);
                    if (IPEndPoint.TryParse(p.EndPoint, out var ep)) Network.Connect(ep, target);
                    ScheduleRelayFallback(target);
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
                while (!_cts.IsCancellationRequested && !_peers.ContainsKey(target))
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

    private async Task RunLoopAsync(Func<Task> action, string what, Func<bool>? stop = null)
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
                await Task.Delay(_options.DiscoveryInterval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }
}
