using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Discovery;

public sealed class LocalServiceDiscovery : IPeerDiscovery
{
    private static readonly IPAddress Group = IPAddress.Parse("239.192.152.143");
    private const int MulticastPort = 6771;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReplyEvery = TimeSpan.FromSeconds(2);

    private readonly Socket _socket;
    private readonly int _announcePort;
    private readonly string _cookie = "yaparr-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sendGate = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, long>> _seen = new();
    private readonly ConcurrentDictionary<string, long> _announced = new();
    private readonly IPAddress[] _interfaces;

    public LocalServiceDiscovery(int announcePort, Action<string>? log = null)
    {
        _announcePort = announcePort;
        _log = log;
        _interfaces = LocalIPv4Addresses();

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socket.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            _socket.MulticastLoopback = true;
            foreach (var addr in _interfaces)
            {
                try { _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, addr)); }
                catch (SocketException ex) { _log?.Invoke($"LSD: cannot join multicast on {addr}: {ex.SocketErrorCode}"); }
            }
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task AnnounceAsync(byte[] topic, CancellationToken ct = default)
    {
        var hex = Hex(topic);
        _announced[hex] = 0;
        Send(hex, reply: false);
        await Task.Delay(400, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default)
    {
        var found = new List<IPEndPoint>();
        if (_seen.TryGetValue(Hex(topic), out var table))
        {
            long cutoff = Environment.TickCount64 - (long)Fresh.TotalMilliseconds;
            foreach (var (ep, at) in table)
            {
                if (at < cutoff) table.TryRemove(ep, out _);
                else found.Add(ep);
            }
        }
        return Task.FromResult<IReadOnlyList<IPEndPoint>>(found);
    }

    private void Send(string topicHex, bool reply)
    {
        var message = Encoding.ASCII.GetBytes(
            $"BT-SEARCH * HTTP/1.1\r\nHost: {Group}:{MulticastPort}\r\nPort: {_announcePort}\r\nInfohash: {topicHex}\r\n" +
            $"cookie: {_cookie}\r\n{(reply ? "x-yaparr-reply: 1\r\n" : "")}\r\n\r\n");
        var target = new IPEndPoint(Group, MulticastPort);

        lock (_sendGate)
        {
            foreach (var addr in _interfaces.DefaultIfEmpty(IPAddress.Any))
            {
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                    _socket.SendTo(message, target);
                }
                catch (SocketException ex) { _log?.Invoke($"LSD: send via {addr} failed: {ex.SocketErrorCode}"); }
                catch (ObjectDisposedException) { return; }
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1500];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                Handle(Encoding.ASCII.GetString(buffer, 0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) {  }
        }
    }

    private void Handle(string text, IPEndPoint from)
    {
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].StartsWith("BT-SEARCH", StringComparison.Ordinal)) return;

        int port = 0; string? cookie = null; bool isReply = false;
        var topics = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (name)
            {
                case "port": int.TryParse(value, out port); break;
                case "infohash": if (value.Length == 40) topics.Add(value.ToLowerInvariant()); break;
                case "cookie": cookie = value; break;
                case "x-yaparr-reply": isReply = true; break;
            }
        }
        if (cookie == _cookie || port is < 1 or > 65535) return;

        var endpoint = new IPEndPoint(from.Address, port);
        foreach (var topic in topics.Take(8))
        {
            _seen.GetOrAdd(topic, _ => new())[endpoint] = Environment.TickCount64;

            if (!isReply && _announced.TryGetValue(topic, out var lastReply) && Environment.TickCount64 - lastReply > ReplyEvery.TotalMilliseconds)
            {
                _announced[topic] = Environment.TickCount64;
                Send(topic, reply: true);
            }
        }
    }

    private static IPAddress[] LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254."))
            .Distinct()
            .ToArray();

    private static string Hex(byte[] topic) => Convert.ToHexString(topic).ToLowerInvariant();

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
    }
}

public sealed class CompositeDiscovery : IPeerDiscovery
{
    private readonly IReadOnlyList<IPeerDiscovery> _members;
    private readonly Action<string>? _log;

    public CompositeDiscovery(IEnumerable<IPeerDiscovery> members, Action<string>? log = null)
    {
        _members = members.ToList();
        _log = log;
    }

    public async Task AnnounceAsync(byte[] topic, CancellationToken ct = default) =>
        await Task.WhenAll(_members.Select(m => Guard(() => m.AnnounceAsync(topic, ct), ct))).ConfigureAwait(false);

    public async Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(_members.Select(async m =>
        {
            IReadOnlyList<IPEndPoint> found = Array.Empty<IPEndPoint>();
            await Guard(async () => found = await m.FindPeersAsync(topic, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return found;
        })).ConfigureAwait(false);
        return results.SelectMany(r => r).Distinct().ToList();
    }

    private async Task Guard(Func<Task> action, CancellationToken ct)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log?.Invoke($"discovery member failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        foreach (var m in _members) m.Dispose();
    }
}
