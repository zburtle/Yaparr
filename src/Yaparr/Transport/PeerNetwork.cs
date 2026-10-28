using System.Collections.Concurrent;
using System.Net;
using Yaparr.Identity;

namespace Yaparr.Transport;

public sealed class PeerNetwork : IDisposable
{
    public const byte RelayChannel = 2;
    private const byte RelayTo = 1, RelayFrom = 2;
    private const int IdLength = 32;

    private readonly PeerIdentity _identity;
    private readonly UdpMux _mux;
    private readonly PeerNetworkOptions _options;
    private readonly byte[] _instance = System.Security.Cryptography.RandomNumberGenerator.GetBytes(PeerConnection.InstanceIdLength);
    private readonly ConcurrentDictionary<IPEndPoint, PeerConnection> _byEndPoint = new();
    private readonly ConcurrentDictionary<PeerId, PeerConnection> _byPeer = new();
    private readonly ConcurrentDictionary<PeerId, PeerConnection> _relayed = new();
    private readonly ConcurrentDictionary<IPEndPoint, PeerId> _endpointOwner = new();
    private readonly ConcurrentDictionary<byte, DataReceivedHandler> _channels = new();
    private readonly object _relayGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _timer;

    public PeerNetwork(PeerIdentity identity, UdpMux mux, PeerNetworkOptions? options = null)
    {
        _identity = identity;
        _mux = mux;
        _options = options ?? new PeerNetworkOptions();
        _channels[RelayChannel] = OnRelayFrame;
        _mux.AddHandler(HandlePacket);
        _timer = Task.Run(TimerLoopAsync);
    }

    public IEnumerable<PeerConnection> Connections => _byPeer.Values;

    public event Action<PeerConnection>? ConnectionEstablished;

    public event Action<PeerConnection>? ConnectionClosed;

    /// <summary>A connection we started was closed before the other side proved who it was (nobody answered, or it timed out).</summary>
    public event Action<IPEndPoint, string?>? ConnectFailed;

    public Func<PeerConnection, PeerConnection, bool>? RelayForwardPolicy { get; set; }

    public Func<PeerConnection, PeerId, bool>? RelayedAcceptPolicy { get; set; }

    public PeerConnection? Get(PeerId peer) => _byPeer.TryGetValue(peer, out var c) ? c : null;

    public void RegisterChannel(byte channel, DataReceivedHandler handler)
    {
        if (channel == RelayChannel) throw new ArgumentException("Channel is reserved for relaying.", nameof(channel));
        if (!_channels.TryAdd(channel, handler)) throw new InvalidOperationException($"Channel {channel} is already registered.");
    }

    public PeerConnection Connect(IPEndPoint endPoint, PeerId? expected = null)
    {
        if (_byEndPoint.TryGetValue(endPoint, out var existing) && existing.State != ConnectionState.Closed) return existing;
        if (_endpointOwner.TryGetValue(endPoint, out var owner) && _byPeer.TryGetValue(owner, out var live) &&
            live.State == ConnectionState.Established && (expected == null || expected == owner)) return live;
        var conn = new PeerConnection(_identity, endPoint, expected, (packet, to) => _mux.Send(packet, to), _options, _instance);
        Hook(conn);
        _byEndPoint[endPoint] = conn;
        return conn;
    }

    public PeerConnection ConnectVia(PeerConnection relay, PeerId target)
    {
        if (relay.IsRelayed || relay.State != ConnectionState.Established || relay.RemoteId is not { } relayId)
            throw new InvalidOperationException("A relay must be an established direct connection.");

        lock (_relayGate)
        {
            if (_relayed.TryGetValue(target, out var existing) && existing.State != ConnectionState.Closed) return existing;
            return CreateRelayed(relay, relayId, target);
        }
    }

    private PeerConnection CreateRelayed(PeerConnection relay, PeerId relayId, PeerId target)
    {
        var conn = new PeerConnection(_identity, relay.RemoteEndPoint, target,
            (packet, _) => SendViaRelay(relay, target, packet), _options, _instance, relayedVia: relayId);
        Hook(conn);
        _relayed[target] = conn;
        return conn;
    }

    private void Hook(PeerConnection conn)
    {
        conn.Connected += OnConnected;
        conn.Closed += OnClosed;
        conn.DataReceived += OnData;
    }

    private static void SendViaRelay(PeerConnection relay, PeerId target, byte[] packet)
    {
        var frame = new byte[1 + IdLength + packet.Length];
        frame[0] = RelayTo;
        Convert.FromHexString(target.Value).CopyTo(frame, 1);
        packet.CopyTo(frame, 1 + IdLength);
        try { relay.SendUnreliable(RelayChannel, frame); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) {  }
    }

    private void OnRelayFrame(PeerConnection from, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (reliable || from.IsRelayed || from.RemoteId is not { } fromId || data.Length < 1 + IdLength + 2) return;
        var peerBytes = data.Span.Slice(1, IdLength);
        var peer = new PeerId(Convert.ToHexString(peerBytes).ToLowerInvariant());
        var payload = data[(1 + IdLength)..];

        switch (data.Span[0])
        {
            case RelayTo:
                if (Get(peer) is { IsRelayed: false, State: ConnectionState.Established } dst
                    && RelayForwardPolicy?.Invoke(from, dst) == true)
                {
                    var frame = new byte[data.Length];
                    frame[0] = RelayFrom;
                    Convert.FromHexString(fromId.Value).CopyTo(frame, 1);
                    payload.Span.CopyTo(frame.AsSpan(1 + IdLength));
                    try { dst.SendUnreliable(RelayChannel, frame); } catch (InvalidOperationException) { }
                }
                break;

            case RelayFrom:
                DeliverRelayed(from, fromId, peer, payload);
                break;
        }
    }

    private void DeliverRelayed(PeerConnection relay, PeerId relayId, PeerId origin, ReadOnlyMemory<byte> payload)
    {
        PeerConnection? conn;
        lock (_relayGate)
        {
            _relayed.TryGetValue(origin, out conn);
            if (conn != null && (conn.State == ConnectionState.Closed || conn.RelayedVia != relayId)) conn = null;
            if (conn == null)
            {
                if (payload.Span[0] != PeerConnection.Magic || payload.Span[1] != 1) return;
                if (Get(origin) is { IsRelayed: false }) return;
                if (RelayedAcceptPolicy?.Invoke(relay, origin) != true) return;
                conn = CreateRelayed(relay, relayId, origin);
                conn.OnDatagram(payload.Span);
                if (conn.State == ConnectionState.Connecting) conn.Dispose();
                return;
            }
        }
        conn.OnDatagram(payload.Span);
    }

    private bool HandlePacket(ReadOnlyMemory<byte> packet, IPEndPoint from)
    {
        if (packet.Length < 2 || packet.Span[0] != PeerConnection.Magic) return false;

        if (_byEndPoint.TryGetValue(from, out var conn) && conn.State != ConnectionState.Closed)
        {
            conn.OnDatagram(packet.Span);
            return true;
        }

        if (_options.AcceptIncoming && packet.Span[1] == 1)
        {
            var inbound = new PeerConnection(_identity, from, null, (p, to) => _mux.Send(p, to), _options, _instance);
            Hook(inbound);
            _byEndPoint[from] = inbound;
            inbound.OnDatagram(packet.Span);
            if (inbound.State == ConnectionState.Connecting) inbound.Dispose();
        }
        return true;
    }

    private void OnConnected(PeerConnection conn)
    {
        var id = conn.RemoteId!.Value;
        if (!conn.IsRelayed) _endpointOwner[conn.RemoteEndPoint] = id;
        if (_byPeer.TryGetValue(id, out var existing) && !ReferenceEquals(existing, conn) && existing.State == ConnectionState.Established)
        {
            if (!Prefer(conn, existing)) { conn.Close(notifyPeer: true); return; }
            existing.Close(notifyPeer: true);
        }
        _byPeer[id] = conn;
        ConnectionEstablished?.Invoke(conn);
    }

    private static bool Prefer(PeerConnection candidate, PeerConnection current)
    {
        if (candidate.IsRelayed != current.IsRelayed) return !candidate.IsRelayed;
        if (Environment.TickCount64 - current.LastReceivedAt > 15_000) return true;
        // A different app run of the same peer: the old session belongs to a process that no longer exists.
        if (candidate.RemoteInstance != null && current.RemoteInstance != null
            && !candidate.RemoteInstance.AsSpan().SequenceEqual(current.RemoteInstance)) return true;
        return candidate.SessionId.AsSpan().SequenceCompareTo(current.SessionId) < 0;
    }

    private void OnClosed(PeerConnection conn)
    {
        _byEndPoint.TryRemove(new KeyValuePair<IPEndPoint, PeerConnection>(conn.RemoteEndPoint, conn));
        if (conn.IsRelayed && conn.Expected is { } target)
            _relayed.TryRemove(new KeyValuePair<PeerId, PeerConnection>(target, conn));

        if (conn.RemoteId is not { } id)
        {
            if (!conn.IsRelayed) ConnectFailed?.Invoke(conn.RemoteEndPoint, conn.CloseReason);
            return;
        }
        bool wasLive = _byPeer.TryRemove(new KeyValuePair<PeerId, PeerConnection>(id, conn));

        if (!conn.IsRelayed)
            foreach (var r in _relayed.Values.Where(r => r.RelayedVia == id).ToList()) r.Close(notifyPeer: false);

        if (wasLive) ConnectionClosed?.Invoke(conn);
    }

    private void OnData(PeerConnection conn, byte channel, ReadOnlyMemory<byte> data, bool reliable)
    {
        if (_channels.TryGetValue(channel, out var handler)) handler(conn, channel, data, reliable);
    }

    private async Task TimerLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                long now = Environment.TickCount64;
                foreach (var conn in _byEndPoint.Values.Concat(_relayed.Values))
                {
                    try { conn.Tick(now); }
                    catch (Exception ex) { _mux.Log?.Invoke($"tick error for {conn.RemoteEndPoint}: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var conn in _byEndPoint.Values.Concat(_relayed.Values).ToList()) conn.Close();
    }
}
