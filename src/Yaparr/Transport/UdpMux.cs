using System.Net;
using System.Net.Sockets;

namespace Yaparr.Transport;

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

    public void AddHandler(PacketHandler handler)
    {
        lock (_handlers) _handlers.Add(handler);
    }

    public void Start() => _receiveLoop ??= Task.Run(ReceiveLoopAsync);

    public void Send(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (OutboundFilter is { } filter && !filter(packet.ToArray(), to)) return;
        try { _socket.SendTo(packet, SocketFlags.None, to); }
        catch (SocketException ex) { Log?.Invoke($"send to {to} failed: {ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { }
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
                var packet = buffer.AsMemory(0, result.ReceivedBytes);
                PacketHandler[] handlers;
                lock (_handlers) handlers = _handlers.ToArray();
                foreach (var h in handlers)
                {
                    try { if (h(packet, from)) break; }
                    catch (Exception ex) { Log?.Invoke($"handler error from {from}: {ex.Message}"); }
                }
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
