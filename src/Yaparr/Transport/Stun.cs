using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Yaparr.Transport;

internal static class StunType
{
    public const ushort BindingRequest = 0x0001, BindingSuccess = 0x0101;
    public const ushort AllocateRequest = 0x0003, AllocateSuccess = 0x0103, AllocateError = 0x0113;
    public const ushort RefreshRequest = 0x0004, RefreshSuccess = 0x0104, RefreshError = 0x0114;
    public const ushort SendIndication = 0x0016, DataIndication = 0x0017;
    public const ushort CreatePermissionRequest = 0x0008, CreatePermissionSuccess = 0x0108, CreatePermissionError = 0x0118;

    public static bool IsError(ushort type) => (type & 0x0110) == 0x0110;
}

internal static class StunAttr
{
    public const ushort MappedAddress = 0x0001, Username = 0x0006, MessageIntegrity = 0x0008, ErrorCode = 0x0009;
    public const ushort Lifetime = 0x000D, XorPeerAddress = 0x0012, Data = 0x0013, Realm = 0x0014, Nonce = 0x0015;
    public const ushort XorRelayedAddress = 0x0016, RequestedTransport = 0x0019, XorMappedAddress = 0x0020;
}

internal sealed class StunMessage
{
    public const uint Cookie = 0x2112A442;
    public const int HeaderLength = 20;

    private readonly List<(ushort Type, byte[] Value)> _attributes = new();

    public StunMessage(ushort type, byte[]? transactionId = null)
    {
        Type = type;
        TransactionId = transactionId ?? RandomNumberGenerator.GetBytes(12);
    }

    public ushort Type { get; }
    public byte[] TransactionId { get; }
    public IReadOnlyList<(ushort Type, byte[] Value)> Attributes => _attributes;

    public static bool LooksLikeStun(ReadOnlySpan<byte> packet) =>
        packet.Length >= HeaderLength && (packet[0] & 0xC0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]) == Cookie;

    public StunMessage Add(ushort type, byte[] value)
    {
        _attributes.Add((type, value));
        return this;
    }

    public StunMessage Add(ushort type, string value) => Add(type, Encoding.UTF8.GetBytes(value));

    public StunMessage AddXorAddress(ushort type, IPEndPoint endPoint) => Add(type, EncodeXorAddress(endPoint, TransactionId));

    public byte[]? Get(ushort type)
    {
        foreach (var a in _attributes) if (a.Type == type) return a.Value;
        return null;
    }

    public string? GetString(ushort type) => Get(type) is { } v ? Encoding.UTF8.GetString(v) : null;

    public IPEndPoint? GetXorAddress(ushort type) => Get(type) is { } v ? DecodeXorAddress(v, TransactionId) : null;

    public IPEndPoint? GetMappedAddress() =>
        GetXorAddress(StunAttr.XorMappedAddress) ?? (Get(StunAttr.MappedAddress) is { } v ? DecodeAddress(v) : null);

    public int ErrorCode()
    {
        var v = Get(StunAttr.ErrorCode);
        return v is { Length: >= 4 } ? (v[2] & 7) * 100 + v[3] : 0;
    }

    public static byte[] ErrorValue(int code, string reason)
    {
        var text = Encoding.UTF8.GetBytes(reason);
        var b = new byte[4 + text.Length];
        b[2] = (byte)(code / 100);
        b[3] = (byte)(code % 100);
        text.CopyTo(b, 4);
        return b;
    }

    public static byte[] LongTermKey(string username, string realm, string password) =>
        MD5.HashData(Encoding.UTF8.GetBytes($"{username}:{realm}:{password}"));

    public byte[] Encode(byte[]? integrityKey = null)
    {
        int attrLength = 0;
        foreach (var a in _attributes) attrLength += 4 + Padded(a.Value.Length);
        int total = HeaderLength + attrLength + (integrityKey != null ? 24 : 0);
        var buffer = new byte[total];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, Type);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), (ushort)(total - HeaderLength));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), Cookie);
        TransactionId.CopyTo(buffer, 8);

        int pos = HeaderLength;
        foreach (var (type, value) in _attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos), type);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos + 2), (ushort)value.Length);
            value.CopyTo(buffer, pos + 4);
            pos += 4 + Padded(value.Length);
        }

        if (integrityKey != null)
        {
            var mac = HMACSHA1.HashData(integrityKey, buffer.AsSpan(0, pos));
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos), StunAttr.MessageIntegrity);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos + 2), 20);
            mac.CopyTo(buffer, pos + 4);
        }
        return buffer;
    }

    public static StunMessage? Decode(ReadOnlySpan<byte> packet)
    {
        if (!LooksLikeStun(packet)) return null;
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if (HeaderLength + length > packet.Length || (length & 3) != 0) return null;

        var message = new StunMessage(BinaryPrimitives.ReadUInt16BigEndian(packet), packet.Slice(8, 12).ToArray());
        int pos = HeaderLength, end = HeaderLength + length;
        while (pos + 4 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[pos..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(packet[(pos + 2)..]);
            if (pos + 4 + len > end) return null;
            message._attributes.Add((type, packet.Slice(pos + 4, len).ToArray()));
            pos += 4 + Padded(len);
        }
        return message;
    }

    public static bool VerifyIntegrity(ReadOnlySpan<byte> packet, byte[] key)
    {
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        int pos = HeaderLength, end = HeaderLength + length;
        while (pos + 4 <= end)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[pos..]);
            int len = BinaryPrimitives.ReadUInt16BigEndian(packet[(pos + 2)..]);
            if (type == StunAttr.MessageIntegrity && len == 20 && pos + 24 <= end)
            {
                var copy = packet[..pos].ToArray();
                BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), (ushort)(pos + 24 - HeaderLength));
                var expected = HMACSHA1.HashData(key, copy);
                return CryptographicOperations.FixedTimeEquals(expected, packet.Slice(pos + 4, 20));
            }
            pos += 4 + Padded(len);
        }
        return false;
    }

    private static int Padded(int length) => (length + 3) & ~3;

    public static byte[] EncodeXorAddress(IPEndPoint endPoint, byte[] transactionId)
    {
        var addr = endPoint.Address.GetAddressBytes();
        var b = new byte[4 + addr.Length];
        b[1] = addr.Length == 4 ? (byte)1 : (byte)2;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)(endPoint.Port ^ (int)(Cookie >> 16)));
        var mask = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mask, Cookie);
        transactionId.CopyTo(mask, 4);
        for (int i = 0; i < addr.Length; i++) b[4 + i] = (byte)(addr[i] ^ mask[i]);
        return b;
    }

    public static IPEndPoint? DecodeXorAddress(byte[] v, byte[] transactionId)
    {
        if (v.Length < 8) return null;
        int length = v[1] == 1 ? 4 : v[1] == 2 ? 16 : 0;
        if (length == 0 || v.Length < 4 + length) return null;
        var mask = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(mask, Cookie);
        transactionId.CopyTo(mask, 4);
        var addr = new byte[length];
        for (int i = 0; i < length; i++) addr[i] = (byte)(v[4 + i] ^ mask[i]);
        int port = BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(2)) ^ (int)(Cookie >> 16);
        return new IPEndPoint(new IPAddress(addr), port);
    }

    public static IPEndPoint? DecodeAddress(byte[] v)
    {
        if (v.Length < 8 || v[1] != 1) return null;
        return new IPEndPoint(new IPAddress(v.AsSpan(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(v.AsSpan(2)));
    }

    public static byte[] EncodeAddress(IPEndPoint endPoint)
    {
        var b = new byte[8];
        b[1] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)endPoint.Port);
        endPoint.Address.GetAddressBytes().CopyTo(b, 4);
        return b;
    }
}

internal sealed class StunTransactions : IDisposable
{
    private sealed record Pending(IPEndPoint From, TaskCompletionSource<StunMessage> Completion);

    private readonly UdpMux _mux;
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public StunTransactions(UdpMux mux)
    {
        _mux = mux;
        _mux.AddHandler(Handle);
    }

    public TimeSpan InitialRetransmit { get; init; } = TimeSpan.FromMilliseconds(400);
    public int Attempts { get; init; } = 3;

    public async Task<StunMessage?> RequestAsync(StunMessage request, IPEndPoint to, byte[]? integrityKey, CancellationToken ct)
    {
        var key = Convert.ToHexString(request.TransactionId);
        var tcs = new TaskCompletionSource<StunMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = new Pending(to, tcs);
        try
        {
            var bytes = request.Encode(integrityKey);
            var wait = InitialRetransmit;
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                _mux.Send(bytes, to);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(wait, ct)).ConfigureAwait(false);
                if (done == tcs.Task) return await tcs.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                wait *= 2;
            }
            return null;
        }
        finally { _pending.TryRemove(key, out _); }
    }

    private bool Handle(ReadOnlyMemory<byte> packet, IPEndPoint from)
    {
        if (!StunMessage.LooksLikeStun(packet.Span)) return false;
        var msg = StunMessage.Decode(packet.Span);
        if (msg == null) return true;
        if (_pending.TryGetValue(Convert.ToHexString(msg.TransactionId), out var p) && p.From.Equals(from))
        {
            p.Completion.TrySetResult(msg);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        foreach (var p in _pending.Values) p.Completion.TrySetCanceled();
    }
}

public enum NatType { Unknown, Open, EndpointIndependent, Symmetric }

public sealed record NatReport(NatType Type, IPEndPoint? PublicEndPoint, IReadOnlyList<IPEndPoint> Mappings)
{
    public static NatReport Unknown { get; } = new(NatType.Unknown, null, Array.Empty<IPEndPoint>());

    public string Describe() => Type switch
    {
        NatType.Open => $"public address {PublicEndPoint}",
        NatType.EndpointIndependent => $"NAT with stable port mapping ({PublicEndPoint})",
        NatType.Symmetric => "symmetric NAT (direct connections to strangers are unlikely)",
        _ => PublicEndPoint != null ? $"NAT of unknown type ({PublicEndPoint})" : "NAT type unknown",
    };
}

public static class NatProbe
{
    public static readonly IReadOnlyList<string> DefaultServers = new[]
    {
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun.cloudflare.com:3478",
    };

    public static async Task<NatReport> RunAsync(UdpMux mux, IEnumerable<string> servers, CancellationToken ct = default)
    {
        using var transactions = new StunTransactions(mux);
        var endpoints = new List<IPEndPoint>();
        foreach (var server in servers)
        {
            try
            {
                var (host, port) = TurnClient.SplitHostPort(server, 3478);
                var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                if (addrs.Length == 0) continue;
                var candidate = new IPEndPoint(addrs[0], port);
                if (!endpoints.Contains(candidate)) endpoints.Add(candidate);
            }
            catch (SocketException) { }
        }

        var replies = await Task.WhenAll(endpoints.Select(async ep =>
        {
            var reply = await transactions.RequestAsync(new StunMessage(StunType.BindingRequest), ep, null, ct).ConfigureAwait(false);
            return reply is { } r && r.Type == StunType.BindingSuccess ? r.GetMappedAddress() : null;
        })).ConfigureAwait(false);

        return Classify(replies.Where(r => r != null).Select(r => r!).ToList(), LocalAddresses());
    }

    internal static NatReport Classify(IReadOnlyList<IPEndPoint> mappings, IReadOnlyCollection<IPAddress> localAddresses)
    {
        if (mappings.Count == 0) return NatReport.Unknown;
        var first = mappings[0];
        if (mappings.Any(m => !m.Equals(first))) return new NatReport(NatType.Symmetric, first, mappings);
        if (localAddresses.Contains(first.Address)) return new NatReport(NatType.Open, first, mappings);
        return mappings.Count >= 2
            ? new NatReport(NatType.EndpointIndependent, first, mappings)
            : new NatReport(NatType.Unknown, first, mappings);
    }

    private static List<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .ToList();
        }
        catch (NetworkInformationException) { return new(); }
    }
}
