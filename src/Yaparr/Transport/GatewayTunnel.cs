using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using Yaparr.Vpn;

namespace Yaparr.Transport;

/// <summary>
/// The frames of the Yaparr Cloud UDP gateway (Yaparr.Cloud README, "UDP gateway"): one WebSocket message is one frame.
/// <c>0x01 DATA | family (4 or 6) | port (2, big endian) | address | datagram</c> goes to the service (where to send it) and comes back (who it came from);
/// <c>0x10 HELLO</c> (same layout, no datagram) tells the address the world is likely to see; <c>0x11 LIMIT | reason</c> says a datagram was not sent.
/// </summary>
public static class GatewayFrames
{
    public const byte Data = 0x01, Hello = 0x10, Limit = 0x11;
    public const byte ReasonRate = 1, ReasonDay = 2, ReasonDestination = 3, ReasonSize = 4;

    /// <summary>The largest datagram the service carries.</summary>
    public const int MaxPacket = 1400;

    public static byte[] EncodeData(IPEndPoint to, ReadOnlySpan<byte> datagram)
    {
        var address = to.Address.IsIPv4MappedToIPv6 ? to.Address.MapToIPv4() : to.Address;
        bool v6 = address.AddressFamily == AddressFamily.InterNetworkV6;
        int header = 1 + 1 + 2 + (v6 ? 16 : 4);
        var frame = new byte[header + datagram.Length];
        frame[0] = Data;
        frame[1] = v6 ? (byte)6 : (byte)4;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)to.Port);
        address.TryWriteBytes(frame.AsSpan(4), out _);
        datagram.CopyTo(frame.AsSpan(header));
        return frame;
    }

    /// <summary>Reads a DATA or HELLO frame. False for anything else.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out byte type, out IPEndPoint endpoint, out int datagramStart)
    {
        type = 0;
        endpoint = null!;
        datagramStart = 0;
        if (frame.Length < 8 || frame[0] is not (Data or Hello)) return false;
        int addressLength = frame[1] switch { 4 => 4, 6 => 16, _ => 0 };
        if (addressLength == 0 || frame.Length < 4 + addressLength) return false;
        type = frame[0];
        endpoint = new IPEndPoint(new IPAddress(frame.Slice(4, addressLength)), BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(2, 2)));
        datagramStart = 4 + addressLength;
        return true;
    }

    /// <summary>An address that is on this network or this machine: it is never sent through the service (which refuses it anyway), but directly.</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] is >= 224;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) || address.IsIPv6Multicast;
        }
        return true;
    }
}

/// <summary>The connection to the gateway, so the tunnel can be tested without a network: connect, send a frame, receive one (null when it is closed).</summary>
public interface IGatewayLink : IDisposable
{
    Task ConnectAsync(CancellationToken cancel);
    Task SendAsync(byte[] frame, CancellationToken cancel);
    Task<byte[]?> ReceiveAsync(CancellationToken cancel);
}

/// <summary><see cref="IGatewayLink"/> as a WebSocket to <c>/v1/gateway</c> of a Yaparr Cloud server, with this device's sign-in. https becomes wss.</summary>
public sealed class WebSocketGatewayLink : IGatewayLink
{
    private readonly Uri _address;
    private readonly string _token;
    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _buffer = new byte[GatewayFrames.MaxPacket + 128];

    public WebSocketGatewayLink(Uri server, string token)
    {
        var builder = new UriBuilder(new Uri(server, "/v1/gateway")) { Scheme = server.Scheme == Uri.UriSchemeHttp ? "ws" : "wss" };
        _address = builder.Uri;
        _token = token;
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    public Task ConnectAsync(CancellationToken cancel) => _socket.ConnectAsync(_address, cancel);

    public Task SendAsync(byte[] frame, CancellationToken cancel) => _socket.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, cancel);

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancel)
    {
        var result = await _socket.ReceiveAsync(_buffer, cancel).ConfigureAwait(false);
        if (result.MessageType == WebSocketMessageType.Close) return null;
        if (!result.EndOfMessage) return Array.Empty<byte>();     // more than a datagram: not one of ours; skipped
        return _buffer.AsSpan(0, result.Count).ToArray();
    }

    public void Dispose() => _socket.Dispose();
}

/// <summary>
/// For a network that blocks UDP: the datagrams Yaparr would send go to a Yaparr Cloud server inside a WebSocket on port 443, which sends them from a UDP socket of its own and hands back
/// what arrives there (see the Yaparr.Cloud README). Everything above UDP (finding people, connecting, the chat protocol) works unchanged, with the server's address as the one other
/// people see. It is capped on the server (messages only at first), so what does not fit is dropped there and told here (<see cref="Limited"/>). It reconnects by itself, with a
/// wait that grows. Public addresses go through it; addresses on this network or machine are sent directly, as before.
/// </summary>
public sealed class GatewayTunnel : ITunnel, IDisposable
{
    private readonly Func<IGatewayLink> _links;
    private readonly UdpMux _mux;
    private readonly Action<string>? _log;
    private readonly string _what;
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;
    private volatile VpnState _state = VpnState.Disconnected;
    private long _lastLimitLogged;

    public GatewayTunnel(Func<IGatewayLink> links, UdpMux mux, Action<string>? log = null, string what = "Yaparr Cloud")
    {
        _links = links;
        _mux = mux;
        _log = log;
        _what = what;
    }

    public VpnState State => _state;

    /// <summary>The address the service says the world is likely to see for this connection (it may be a hint only).</summary>
    public IPEndPoint? PublicHint { get; private set; }

    public event Action<VpnState>? StateChanged;

    /// <summary>The service did not send, or hand on, a datagram: the reason (see <see cref="GatewayFrames"/>). At most one a second.</summary>
    public event Action<byte>? Limited;

    public string Describe() => _state switch
    {
        VpnState.Connected => $"{_what} gateway (messages)",
        VpnState.Connecting => $"{_what} gateway connecting…",
        _ => "gateway off",
    };

    private void SetState(VpnState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>Starts, and waits for the connection. False if it was not up in time; it keeps trying.</summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _run ??= Task.Run(() => RunAsync(_cts.Token));
        var until = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (_state != VpnState.Connected && Environment.TickCount64 < until && !_cts.IsCancellationRequested)
            await Task.Delay(50, ct).ConfigureAwait(false);
        return _state == VpnState.Connected;
    }

    public bool TrySend(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (GatewayFrames.IsLocal(to.Address)) return false;                  // this network, this machine: directly
        // A public address goes through the service or nowhere: this tunnel is only in use because UDP out of here does not work.
        if (_state == VpnState.Connected && packet.Length > 0 && packet.Length <= GatewayFrames.MaxPacket)
            _outgoing.Writer.TryWrite(GatewayFrames.EncodeData(to, packet));
        return true;
    }

    private async Task RunAsync(CancellationToken cancel)
    {
        var wait = TimeSpan.FromSeconds(2);
        while (!cancel.IsCancellationRequested)
        {
            SetState(VpnState.Connecting);
            using var link = _links();
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await link.ConnectAsync(connect.Token).ConfigureAwait(false);
                SetState(VpnState.Connected);
                wait = TimeSpan.FromSeconds(2);
                _log?.Invoke($"{_what} gateway connected");
                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                var send = SendLoopAsync(link, session.Token);
                var receive = ReceiveLoopAsync(link, session.Token);
                await Task.WhenAny(send, receive).ConfigureAwait(false);
                session.Cancel();
                try { await Task.WhenAll(send, receive).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or HttpRequestException or IOException or ObjectDisposedException or SocketException)
            {
                if (cancel.IsCancellationRequested) break;
                _log?.Invoke($"{_what} gateway: {ex.Message}");
            }
            if (cancel.IsCancellationRequested) break;
            SetState(VpnState.Connecting);
            try { await Task.Delay(wait, cancel).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            wait = TimeSpan.FromSeconds(Math.Min(60, wait.TotalSeconds * 2));
        }
        SetState(VpnState.Disconnected);
    }

    private async Task SendLoopAsync(IGatewayLink link, CancellationToken cancel)
    {
        await foreach (var frame in _outgoing.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
            await link.SendAsync(frame, cancel).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(IGatewayLink link, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            var frame = await link.ReceiveAsync(cancel).ConfigureAwait(false);
            if (frame == null) return;
            if (frame.Length == 0) continue;
            if (frame[0] == GatewayFrames.Limit)
            {
                if (frame.Length >= 2) NoteLimit(frame[1]);
                continue;
            }
            if (!GatewayFrames.TryDecode(frame, out var type, out var endpoint, out var start)) continue;
            if (type == GatewayFrames.Hello) { PublicHint = endpoint; continue; }
            _mux.Inject(frame.AsMemory(start), endpoint, viaRelay: false);
        }
    }

    private void NoteLimit(byte reason)
    {
        Limited?.Invoke(reason);
        var now = Environment.TickCount64;
        if (now - _lastLimitLogged < 30_000) return;
        _lastLimitLogged = now;
        _log?.Invoke($"{_what} gateway held something back ({reason switch { GatewayFrames.ReasonRate => "speed", GatewayFrames.ReasonDay => "the day's limit", GatewayFrames.ReasonDestination => "destination", GatewayFrames.ReasonSize => "size", _ => "limit" }})");
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        SetState(VpnState.Disconnected);
    }
}
