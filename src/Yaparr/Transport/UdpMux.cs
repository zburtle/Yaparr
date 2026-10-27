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
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            _socket.IOControl(-1744830452, new byte[] { 0 }, null);
        }
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
        LocalPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

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
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                var from = (IPEndPoint)result.RemoteEndPoint;
                Deliver(buffer.AsMemory(0, result.ReceivedBytes), from, viaRelay: false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { Log?.Invoke($"receive error: {ex.SocketErrorCode}"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
    }
}
