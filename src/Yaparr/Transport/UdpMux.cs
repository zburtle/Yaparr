using System.Net;
using System.Net.Sockets;

namespace Yaparr.Transport;

public enum RouteMode { Direct, Relay, Both }

public interface IPacketRoute
{
    RouteMode Choose(IPEndPoint to, ReadOnlySpan<byte> packet);
    void SendRelayed(ReadOnlySpan<byte> packet, IPEndPoint to);
    void NoteReceived(IPEndPoint from, bool viaRelay);
}

public interface ITunnel
{
    bool TrySend(ReadOnlySpan<byte> packet, IPEndPoint to);
}

public sealed class UdpMux : IDisposable
{
    public delegate bool PacketHandler(ReadOnlyMemory<byte> packet, IPEndPoint from);

    private readonly Socket _socket;
    private readonly List<PacketHandler> _handlers = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;

    public UdpMux(int port = 0)
    {
        _socket = OpenSocket(port, out bool dualStack);
        DualStack = dualStack;
        LocalPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Opens one socket for both families when the system allows it (IPv4 peers then appear as ordinary IPv4 addresses);
    /// otherwise an IPv4-only socket, as before.
    /// </summary>
    private static Socket OpenSocket(int port, out bool dualStack)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            try
            {
                if (OperatingSystem.IsWindows()) socket.IOControl(-1744830452, new byte[] { 0 }, null);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                dualStack = true;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        {
            dualStack = false;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (OperatingSystem.IsWindows()) socket.IOControl(-1744830452, new byte[] { 0 }, null);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return socket;
        }
    }

    /// <summary>True when the socket also sends and receives IPv6.</summary>
    public bool DualStack { get; }

    public int LocalPort { get; }

    public Action<string>? Log { get; set; }

    public Func<ReadOnlyMemory<byte>, IPEndPoint, bool>? OutboundFilter { get; set; }

    public IPacketRoute? Route { get; set; }

    public ITunnel? Tunnel { get; set; }

    public int? AdvertisedPort { get; set; }

    public void AddHandler(PacketHandler handler)
    {
        lock (_handlers) _handlers.Add(handler);
    }

    public void Start() => _receiveLoop ??= Task.Run(ReceiveLoopAsync);

    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (OutboundFilter is { } filter && !filter(packet.ToArray(), to)) return;
        var mode = Route?.Choose(to, packet) ?? RouteMode.Direct;
        if (mode != RouteMode.Relay) SendDirect(packet, to);
        if (mode != RouteMode.Direct) Route!.SendRelayed(packet, to);
    }

    public void SendRelayed(ReadOnlySpan<byte> packet, IPEndPoint to) => Route?.SendRelayed(packet, to);

    public void Inject(ReadOnlyMemory<byte> packet, IPEndPoint from, bool viaRelay = true) => Deliver(packet, from, viaRelay);

    /// <summary>
    /// Sends from the socket itself, whatever carries the rest of the traffic: past the tunnel (a VPN, the Yaparr Cloud gateway), the route and the filter. For asking whether UDP gets out
    /// of this network by itself while something is carrying the traffic (<see cref="NatProbe.DirectUdpWorksAsync"/>).
    /// </summary>
    internal void SendUntunneled(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        try { _socket.SendTo(packet, SocketFlags.None, to); }
        catch (SocketException ex) { Log?.Invoke($"direct send to {to} failed: {ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { }
    }

    internal void SendDirect(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (Tunnel?.TrySend(packet, to) == true) return;
        try { _socket.SendTo(packet, SocketFlags.None, to); }
        catch (SocketException ex) { Log?.Invoke($"send to {to} failed: {ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { }
    }

    private void Deliver(ReadOnlyMemory<byte> packet, IPEndPoint from, bool viaRelay)
    {
        Route?.NoteReceived(from, viaRelay);
        PacketHandler[] handlers;
        lock (_handlers) handlers = _handlers.ToArray();
        foreach (var h in handlers)
        {
            try { if (h(packet, from)) break; }
            catch (Exception ex) { Log?.Invoke($"handler error from {from}: {ex.Message}"); }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];
        EndPoint any = new IPEndPoint(DualStack ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                var from = AsCanonical((IPEndPoint)result.RemoteEndPoint);
                Deliver(buffer.AsMemory(0, result.ReceivedBytes), from, viaRelay: false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { Log?.Invoke($"receive error: {ex.SocketErrorCode}"); }
        }
    }

    /// <summary>An IPv4 peer seen through the IPv6 socket is ::ffff:a.b.c.d; report it as the plain IPv4 address everyone else uses.</summary>
    internal static IPEndPoint AsCanonical(IPEndPoint e) =>
        e.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(e.Address.MapToIPv4(), e.Port) : e;

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
    }
}
