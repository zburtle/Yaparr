using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Yaparr.Transport;

public sealed class TurnOptions
{
    public string Server { get; init; } = "";
    public string Username { get; init; } = "";
    public string Credential { get; init; } = "";
    public bool Always { get; init; }
}

public sealed class TurnClient : IPacketRoute, IDisposable
{
    private static readonly long PermissionLifetimeMs = 290_000;
    private const int MaxPermissions = 64;
    private const long RelayFreshMs = 20_000;

    private sealed class Seen
    {
        public long Direct, Relay;
    }

    private readonly UdpMux _mux;
    private readonly TurnOptions _options;
    private readonly Action<string>? _log;
    private readonly StunTransactions _transactions;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPAddress, long> _permissions = new();
    private readonly ConcurrentDictionary<IPAddress, byte> _inFlight = new();
    private readonly ConcurrentDictionary<IPEndPoint, Seen> _seen = new();
    private IPEndPoint? _server;
    private string? _realm, _nonce;
    private byte[]? _key;
    private int _lifetimeSeconds = 600;

    public TurnClient(UdpMux mux, TurnOptions options, Action<string>? log = null)
    {
        _mux = mux;
        _options = options;
        _log = log;
        _transactions = new StunTransactions(mux);
        _mux.AddHandler(HandleIndication);
    }

    public IPEndPoint? RelayedEndPoint { get; private set; }

    public IPEndPoint? ReflexiveEndPoint { get; private set; }

    public bool IsActive => RelayedEndPoint != null && !_cts.IsCancellationRequested;

    public bool RelayLocalAddresses { get; set; }

    public static (string Host, int Port) SplitHostPort(string value, int defaultPort)
    {
        value = value.Trim();
        foreach (var scheme in new[] { "turns:", "turn:", "stuns:", "stun:" })
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) { value = value[scheme.Length..]; break; }
        int q = value.IndexOf('?');
        if (q >= 0) value = value[..q];
        int i = value.LastIndexOf(':');
        return i > 0 && int.TryParse(value[(i + 1)..], out var port) ? (value[..i], port) : (value, defaultPort);
    }

    public async Task<bool> AllocateAsync(CancellationToken ct = default)
    {
        try
        {
            var (host, port) = SplitHostPort(_options.Server, 3478);
            var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
            if (addrs.Length == 0) { _log?.Invoke($"TURN server {_options.Server} did not resolve"); return false; }
            _server = new IPEndPoint(addrs[0], port);
        }
        catch (SocketException ex)
        {
            _log?.Invoke($"TURN server {_options.Server} did not resolve: {ex.Message}");
            return false;
        }

        var reply = await AuthenticatedAsync(
            () => new StunMessage(StunType.AllocateRequest).Add(StunAttr.RequestedTransport, new byte[] { 17, 0, 0, 0 }), ct).ConfigureAwait(false);
        if (reply == null || reply.Type != StunType.AllocateSuccess)
        {
            _log?.Invoke(reply == null
                ? $"TURN server {_server} did not answer"
                : $"TURN allocation refused ({reply.ErrorCode()})");
            return false;
        }

        RelayedEndPoint = reply.GetXorAddress(StunAttr.XorRelayedAddress);
        ReflexiveEndPoint = reply.GetXorAddress(StunAttr.XorMappedAddress);
        if (reply.Get(StunAttr.Lifetime) is { Length: 4 } life)
            _lifetimeSeconds = Math.Max(30, (int)BinaryPrimitives.ReadUInt32BigEndian(life));
        if (RelayedEndPoint == null) return false;

        _mux.Route = this;
        _ = Task.Run(() => MaintainAsync(_cts.Token));
        _log?.Invoke($"TURN relay allocated at {RelayedEndPoint} via {_server}");
        return true;
    }

    public async Task<bool> EnsurePermissionAsync(IPAddress address, CancellationToken ct = default)
    {
        if (HasPermission(address)) return true;
        var reply = await AuthenticatedAsync(
            () => new StunMessage(StunType.CreatePermissionRequest).AddXorAddress(StunAttr.XorPeerAddress, new IPEndPoint(address, 0)), ct).ConfigureAwait(false);
        if (reply?.Type != StunType.CreatePermissionSuccess) return false;
        Remember(address);
        return true;
    }

    private bool HasPermission(IPAddress address) =>
        _permissions.TryGetValue(address, out var until) && until - Environment.TickCount64 > 30_000;

    private void Remember(IPAddress address)
    {
        _permissions[address] = Environment.TickCount64 + PermissionLifetimeMs;
        if (_permissions.Count <= MaxPermissions) return;
        foreach (var oldest in _permissions.OrderBy(kv => kv.Value).Take(_permissions.Count - MaxPermissions))
            _permissions.TryRemove(oldest.Key, out _);
    }

    private void RequestPermissionInBackground(IPAddress address)
    {
        if (!_inFlight.TryAdd(address, 0)) return;
        _ = Task.Run(async () =>
        {
            try { await EnsurePermissionAsync(address, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            finally { _inFlight.TryRemove(address, out _); }
        });
    }

    private async Task<StunMessage?> AuthenticatedAsync(Func<StunMessage> build, CancellationToken ct)
    {
        if (_server == null) return null;
        StunMessage? reply = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var request = build();
            if (_key != null)
                request.Add(StunAttr.Username, _options.Username).Add(StunAttr.Realm, _realm!).Add(StunAttr.Nonce, _nonce!);
            reply = await _transactions.RequestAsync(request, _server, _key, ct).ConfigureAwait(false);
            if (reply == null || !StunType.IsError(reply.Type)) return reply;

            int code = reply.ErrorCode();
            if ((code != 401 && code != 438) || reply.GetString(StunAttr.Nonce) is not { } nonce) return reply;
            _nonce = nonce;
            _realm = reply.GetString(StunAttr.Realm) ?? _realm ?? "";
            _key = StunMessage.LongTermKey(_options.Username, _realm, _options.Credential);
        }
        return reply;
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        long lastRefresh = Environment.TickCount64;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long now = Environment.TickCount64;
                if (now - lastRefresh >= _lifetimeSeconds * 500L)
                {
                    var reply = await AuthenticatedAsync(
                        () => new StunMessage(StunType.RefreshRequest).Add(StunAttr.Lifetime, LifetimeBytes(_lifetimeSeconds)), ct).ConfigureAwait(false);
                    if (reply?.Type == StunType.RefreshSuccess) lastRefresh = now;
                    else _log?.Invoke("TURN refresh failed");
                }

                foreach (var kv in _permissions.ToList())
                    if (kv.Value - now < 90_000) RequestPermissionInBackground(kv.Key);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static byte[] LifetimeBytes(int seconds)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)seconds);
        return b;
    }

    private bool HandleIndication(ReadOnlyMemory<byte> packet, IPEndPoint from)
    {
        if (_server == null || !from.Equals(_server) || !StunMessage.LooksLikeStun(packet.Span)) return false;
        var msg = StunMessage.Decode(packet.Span);
        if (msg is not { Type: StunType.DataIndication }) return false;
        if (msg.GetXorAddress(StunAttr.XorPeerAddress) is { } peer && msg.Get(StunAttr.Data) is { } data)
            _mux.Inject(data, peer);
        return true;
    }

    public RouteMode Choose(IPEndPoint to, ReadOnlySpan<byte> packet)
    {
        if (!IsActive || to.Equals(_server) || packet.Length < 2 || packet[0] != PeerConnection.Magic) return RouteMode.Direct;
        if (!RelayLocalAddresses && IsLocal(to.Address)) return RouteMode.Direct;
        if (packet[1] == 1) return RouteMode.Both;

        if (!_seen.TryGetValue(to, out var seen)) return RouteMode.Direct;
        long now = Environment.TickCount64;
        bool relayWorks = now - Volatile.Read(ref seen.Relay) < RelayFreshMs;
        bool directWorks = now - Volatile.Read(ref seen.Direct) < RelayFreshMs;
        return relayWorks && !directWorks ? RouteMode.Relay : RouteMode.Direct;
    }

    public void SendRelayed(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (!IsActive || _server == null) return;
        if (!HasPermission(to.Address))
        {
            RequestPermissionInBackground(to.Address);
            if (!_permissions.ContainsKey(to.Address)) return;
        }
        var indication = new StunMessage(StunType.SendIndication)
            .AddXorAddress(StunAttr.XorPeerAddress, to)
            .Add(StunAttr.Data, packet.ToArray());
        _mux.SendDirect(indication.Encode(), _server);
    }

    public void NoteReceived(IPEndPoint from, bool viaRelay)
    {
        if (!IsActive || from.Equals(_server)) return;
        var seen = _seen.GetOrAdd(from, _ => new Seen());
        Volatile.Write(ref viaRelay ? ref seen.Relay : ref seen.Direct, Environment.TickCount64);
        if (_seen.Count > 512)
            foreach (var stale in _seen.Where(kv => Environment.TickCount64 - Math.Max(kv.Value.Direct, kv.Value.Relay) > 120_000).Take(128))
                _seen.TryRemove(stale.Key, out _);
    }

    private static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        if (ReferenceEquals(_mux.Route, this)) _mux.Route = null;
        _transactions.Dispose();
    }
}
