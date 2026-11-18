using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Yaparr.Transport;

namespace Yaparr.Vpn;

public enum VpnState { Disconnected, Connecting, Connected }

internal sealed class ReplayWindow
{
    private const int Size = 2048;
    private readonly ulong[] _bits = new ulong[Size / 64];
    private ulong _last;
    private bool _any;

    public bool Accept(ulong counter)
    {
        if (!_any)
        {
            _any = true;
            _last = counter;
            Set(counter);
            return true;
        }
        if (counter > _last)
        {
            ulong diff = counter - _last;
            if (diff >= Size) Array.Clear(_bits);
            else for (ulong i = _last + 1; i <= counter; i++) Clear(i);
            _last = counter;
            Set(counter);
            return true;
        }
        if (_last - counter >= Size || Get(counter)) return false;
        Set(counter);
        return true;
    }

    private void Set(ulong c) => _bits[(c % Size) / 64] |= 1UL << (int)(c % 64);
    private void Clear(ulong c) => _bits[(c % Size) / 64] &= ~(1UL << (int)(c % 64));
    private bool Get(ulong c) => (_bits[(c % Size) / 64] & (1UL << (int)(c % 64))) != 0;
}

public sealed class WireGuardTunnel : ITunnel, IDisposable
{
    private const byte TypeInitiation = 1, TypeResponse = 2, TypeCookie = 3, TypeData = 4;
    private const long RekeyAfterMs = 120_000, RejectAfterMs = 180_000, RetryMs = 3_000, ResolveRetryMs = 5_000;

    private sealed class Session
    {
        public uint LocalIndex, RemoteIndex;
        public required byte[] SendKey, ReceiveKey;
        public long SendCounter;
        public readonly ReplayWindow Replay = new();
        public long CreatedAt;
    }

    private sealed class Pending
    {
        public uint Index;
        public required byte[] EphemeralPrivate, C, H;
    }

    private readonly WireGuardConfig _config;
    private readonly UdpMux _mux;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _publicKey;
    private readonly TaskCompletionSource<bool> _firstHandshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Socket? _socket;
    private IPEndPoint? _server;
    private Session? _current, _previous;
    private Pending? _pending;
    private long _lastInitiation, _lastSend, _lastReceive, _handshakeAt, _lastResolve;
    private int _ipId;
    private bool _oversizeLogged, _familyLogged, _silentLogged;
    private static readonly TimeSpan SilentWarning = TimeSpan.FromSeconds(45);
    private VpnState _state = VpnState.Disconnected;

    public WireGuardTunnel(WireGuardConfig config, UdpMux mux, Action<string>? log = null)
    {
        _config = config;
        _mux = mux;
        _log = log;
        _publicKey = WireGuardCrypto.PublicKey(config.PrivateKey);
    }

    public VpnState State => _state;

    /// <summary>How long nothing at all has come back from the server. Long silence on a connected tunnel usually means another device is using the same configuration.</summary>
    public TimeSpan SilentFor => _state == VpnState.Connected ? TimeSpan.FromMilliseconds(Environment.TickCount64 - Math.Max(_lastReceive, Interlocked.Read(ref _handshakeAt))) : TimeSpan.Zero;

    public IPAddress LocalAddress => _config.Address;

    internal bool TunnelLocalAddresses { get; set; }

    public DateTimeOffset? LastHandshake
    {
        get
        {
            long at = Interlocked.Read(ref _handshakeAt);
            return at == 0 ? null : DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64 - at);
        }
    }

    public event Action<VpnState>? StateChanged;

    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        SetState(VpnState.Connecting);
        _ = Task.Run(TimerLoopAsync);
        // A failed lookup does not throw: the tunnel stays up in the "connecting" state, drops public traffic (the kill
        // switch), and the timer loop keeps trying the lookup until the server's address is known.
        if (await TryResolveServerAsync(ct).ConfigureAwait(false))
            lock (_gate) SendInitiationLocked();
        else
            _log?.Invoke($"VPN: cannot look up {_config.EndpointHost} yet; public traffic stays off until it can be");

        try { await _firstHandshake.Task.WaitAsync(timeout, ct).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private async Task<bool> TryResolveServerAsync(CancellationToken ct)
    {
        var (host, port) = WireGuardConfig.SplitEndpoint(_config.Endpoint);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, ct).ConfigureAwait(false);
            // IPv4 is preferred when the name has both, as before. A server with only an IPv6 address gets an IPv6 socket.
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (address == null) return false;
            lock (_gate)
            {
                if (_cts.IsCancellationRequested) return false;
                _server = new IPEndPoint(address, port);
                _socket ??= OpenSocket(address.AddressFamily);
            }
            return true;
        }
        catch (SocketException) { return false; }
    }

    private Socket OpenSocket(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) socket.IOControl(-1744830452, new byte[] { 0 }, null);
        socket.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        _ = Task.Run(() => ReceiveLoopAsync(socket));
        return socket;
    }

    public bool TrySend(ReadOnlySpan<byte> packet, IPEndPoint to)
    {
        if (!TunnelLocalAddresses && TurnClient.IsLocal(to.Address)) return false;
        // Packets the tunnel cannot carry (no address of that family in the configuration) are dropped, never sent directly.
        var source = _config.AddressFor(to.AddressFamily);
        if (source == null)
        {
            if (!_familyLogged) { _familyLogged = true; _log?.Invoke($"dropping a packet for {to.AddressFamily} destinations: the VPN configuration has no address of that family"); }
            return true;
        }

        bool v6 = to.AddressFamily == AddressFamily.InterNetworkV6;
        int headerLength = v6 ? 40 : 20;
        int total = headerLength + 8 + packet.Length;
        if (total > _config.Mtu)
        {
            if (!_oversizeLogged) { _oversizeLogged = true; _log?.Invoke($"dropping a {packet.Length}-byte packet that does not fit the tunnel MTU"); }
            return true;
        }

        var ip = new byte[total];
        if (v6)
        {
            ip[0] = 0x60;
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), (ushort)(8 + packet.Length));
            ip[6] = 17;
            ip[7] = 64;
            source.GetAddressBytes().CopyTo(ip, 8);
            to.Address.GetAddressBytes().CopyTo(ip, 24);
        }
        else
        {
            ip[0] = 0x45;
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)total);
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), (ushort)Interlocked.Increment(ref _ipId));
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(6), 0x4000);
            ip[8] = 64;
            ip[9] = 17;
            source.GetAddressBytes().CopyTo(ip, 12);
            to.Address.GetAddressBytes().CopyTo(ip, 16);
            BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(10), Checksum(ip.AsSpan(0, 20)));
        }
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(headerLength), (ushort)_mux.LocalPort);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(headerLength + 2), (ushort)to.Port);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(headerLength + 4), (ushort)(8 + packet.Length));
        packet.CopyTo(ip.AsSpan(headerLength + 8));
        // IPv6 has no header checksum, and a UDP checksum is mandatory over IPv6.
        if (v6) BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(headerLength + 6), UdpChecksumV6(ip.AsSpan(8, 16), ip.AsSpan(24, 16), ip.AsSpan(headerLength)));

        lock (_gate)
        {
            if (!HasLiveSessionLocked(out var session))
            {
                MaybeInitiateLocked();
                return true;
            }
            SendDataLocked(session, ip);
            if (Environment.TickCount64 - session.CreatedAt >= RekeyAfterMs) MaybeInitiateLocked();
        }
        return true;
    }

    private static ushort Checksum(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (int i = 0; i < header.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(header[i..]);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    private static ushort UdpChecksumV6(ReadOnlySpan<byte> source, ReadOnlySpan<byte> destination, ReadOnlySpan<byte> udp)
    {
        uint sum = SumWords(source, 0);
        sum = SumWords(destination, sum);
        sum += (uint)udp.Length; // the pseudo-header's UDP length (small, so the high 16 bits are zero)
        sum += 17;               // the pseudo-header's next header: UDP
        sum = SumWords(udp, sum);
        ushort result = Fold(sum);
        return result == 0 ? (ushort)0xFFFF : result;
    }

    private static uint SumWords(ReadOnlySpan<byte> data, uint sum)
    {
        int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }

    private bool HasLiveSessionLocked(out Session session)
    {
        session = _current!;
        return _current != null && Environment.TickCount64 - _current.CreatedAt < RejectAfterMs;
    }

    private void MaybeInitiateLocked()
    {
        if (_server == null || _cts.IsCancellationRequested) return;
        if (Environment.TickCount64 - _lastInitiation < RetryMs) return;
        SendInitiationLocked();
    }

    private void SendInitiationLocked()
    {
        if (_socket == null || _server == null) return;
        var serverKey = _config.PeerPublicKey;
        var c = WireGuardCrypto.Hash(WireGuardCrypto.Construction);
        var h = WireGuardCrypto.Hash(c, WireGuardCrypto.Identifier);
        h = WireGuardCrypto.Hash(h, serverKey);

        var ephemeralPrivate = WireGuardCrypto.GeneratePrivateKey();
        var ephemeralPublic = WireGuardCrypto.PublicKey(ephemeralPrivate);
        c = WireGuardCrypto.Kdf1(c, ephemeralPublic);
        h = WireGuardCrypto.Hash(h, ephemeralPublic);

        var (c1, k1) = WireGuardCrypto.Kdf2(c, WireGuardCrypto.Dh(ephemeralPrivate, serverKey));
        var encryptedStatic = WireGuardCrypto.Seal(k1, 0, _publicKey, h);
        h = WireGuardCrypto.Hash(h, encryptedStatic);

        var (c2, k2) = WireGuardCrypto.Kdf2(c1, WireGuardCrypto.Dh(_config.PrivateKey, serverKey));
        var encryptedTimestamp = WireGuardCrypto.Seal(k2, 0, WireGuardCrypto.Tai64n(DateTimeOffset.UtcNow), h);
        h = WireGuardCrypto.Hash(h, encryptedTimestamp);

        uint index = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var message = new byte[148];
        message[0] = TypeInitiation;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), index);
        ephemeralPublic.CopyTo(message, 8);
        encryptedStatic.CopyTo(message, 40);
        encryptedTimestamp.CopyTo(message, 88);
        var mac1Key = WireGuardCrypto.Hash(WireGuardCrypto.LabelMac1, serverKey);
        WireGuardCrypto.Mac16(mac1Key, message[..116]).CopyTo(message, 116);

        _pending = new Pending { Index = index, EphemeralPrivate = ephemeralPrivate, C = c2, H = h };
        _lastInitiation = Environment.TickCount64;
        Transmit(message);
    }

    private void Transmit(byte[] datagram)
    {
        try { _socket!.SendTo(datagram, _server!); _lastSend = Environment.TickCount64; }
        catch (SocketException ex) { _log?.Invoke($"VPN send failed: {ex.SocketErrorCode}"); }
        catch (ObjectDisposedException) { }
    }

    private void SendDataLocked(Session session, byte[] plaintext)
    {
        int padded = (plaintext.Length + 15) & ~15;
        var body = plaintext;
        if (padded != plaintext.Length) { body = new byte[padded]; plaintext.CopyTo(body, 0); }

        ulong counter = (ulong)session.SendCounter++;
        var sealedBody = WireGuardCrypto.Seal(session.SendKey, counter, body, Array.Empty<byte>());
        var message = new byte[16 + sealedBody.Length];
        message[0] = TypeData;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), session.RemoteIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(8), counter);
        sealedBody.CopyTo(message, 16);
        Transmit(message);
    }

    private async Task ReceiveLoopAsync(Socket socket)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _cts.Token).ConfigureAwait(false);
                if (!((IPEndPoint)result.RemoteEndPoint).Equals(_server) || result.ReceivedBytes < 4) continue;
                Handle(buffer.AsSpan(0, result.ReceivedBytes));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { }
            catch (Exception ex) { _log?.Invoke($"VPN packet error: {ex.Message}"); }
        }
    }

    private void Handle(ReadOnlySpan<byte> message)
    {
        switch (message[0])
        {
            case TypeResponse when message.Length == 92: HandleResponse(message); break;
            case TypeData when message.Length >= 32: HandleData(message); break;
            case TypeCookie: _log?.Invoke("VPN server sent a cookie reply (under load); retrying"); break;
        }
    }

    private void HandleResponse(ReadOnlySpan<byte> message)
    {
        lock (_gate)
        {
            var pending = _pending;
            if (pending == null || BinaryPrimitives.ReadUInt32LittleEndian(message[8..]) != pending.Index) return;

            var mac1Key = WireGuardCrypto.Hash(WireGuardCrypto.LabelMac1, _publicKey);
            if (!CryptographicOperations.FixedTimeEquals(WireGuardCrypto.Mac16(mac1Key, message[..60].ToArray()), message.Slice(60, 16))) return;

            var responderEphemeral = message.Slice(12, 32).ToArray();
            var c = WireGuardCrypto.Kdf1(pending.C, responderEphemeral);
            var h = WireGuardCrypto.Hash(pending.H, responderEphemeral);
            try
            {
                c = WireGuardCrypto.Kdf1(c, WireGuardCrypto.Dh(pending.EphemeralPrivate, responderEphemeral));
                c = WireGuardCrypto.Kdf1(c, WireGuardCrypto.Dh(_config.PrivateKey, responderEphemeral));
            }
            catch (CryptographicException) { return; }

            var (c3, tau, key) = WireGuardCrypto.Kdf3(c, _config.PresharedKey ?? new byte[WireGuardCrypto.KeyLength]);
            h = WireGuardCrypto.Hash(h, tau);
            var encryptedNothing = message.Slice(44, 16);
            if (WireGuardCrypto.Open(key, 0, encryptedNothing, h) is not { Length: 0 }) return;

            var (sendKey, receiveKey) = WireGuardCrypto.Kdf2(c3, Array.Empty<byte>());
            _previous = _current;
            _current = new Session
            {
                LocalIndex = pending.Index,
                RemoteIndex = BinaryPrimitives.ReadUInt32LittleEndian(message[4..]),
                SendKey = sendKey,
                ReceiveKey = receiveKey,
                CreatedAt = Environment.TickCount64,
            };
            _pending = null;
            Interlocked.Exchange(ref _handshakeAt, Environment.TickCount64);
            _lastReceive = Environment.TickCount64;
            SendDataLocked(_current, Array.Empty<byte>());
        }
        SetState(VpnState.Connected);
        _firstHandshake.TrySetResult(true);
    }

    private void HandleData(ReadOnlySpan<byte> message)
    {
        uint index = BinaryPrimitives.ReadUInt32LittleEndian(message[4..]);
        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(message[8..]);
        byte[]? plaintext;

        lock (_gate)
        {
            var session = _current is { } c && c.LocalIndex == index ? c : _previous is { } p && p.LocalIndex == index ? p : null;
            if (session == null) return;
            plaintext = WireGuardCrypto.Open(session.ReceiveKey, counter, message[16..], Array.Empty<byte>());
            if (plaintext == null || !session.Replay.Accept(counter)) return;
            _lastReceive = Environment.TickCount64;
        }
        if (plaintext.Length >= 28) DeliverIp(plaintext);
    }

    private void DeliverIp(byte[] ip)
    {
        if ((ip[0] >> 4) == 6) { DeliverIpv6(ip); return; }
        if ((ip[0] >> 4) != 4) return;
        int headerLength = (ip[0] & 0x0F) * 4;
        int total = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(2));
        if (ip[9] != 17 || headerLength < 20 || total > ip.Length || total < headerLength + 8) return;
        if ((BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(6)) & 0x3FFF) != 0) return;

        int sourcePort = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(headerLength));
        int destinationPort = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(headerLength + 2));
        int length = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(headerLength + 4)) - 8;
        if (destinationPort != _mux.LocalPort || length < 0 || headerLength + 8 + length > total) return;

        var source = new IPEndPoint(new IPAddress(ip.AsSpan(12, 4)), sourcePort);
        _mux.Inject(ip.AsMemory(headerLength + 8, length), source, viaRelay: false);
    }

    private void DeliverIpv6(byte[] ip)
    {
        // Extension headers are not supported: only a UDP packet directly after the 40-byte header is delivered.
        if (ip.Length < 48 || ip[6] != 17) return;
        int total = 40 + BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(4));
        if (total > ip.Length) return;

        int sourcePort = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(40));
        int destinationPort = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(42));
        int length = BinaryPrimitives.ReadUInt16BigEndian(ip.AsSpan(44)) - 8;
        if (destinationPort != _mux.LocalPort || length < 0 || 48 + length > total) return;

        var source = new IPEndPoint(new IPAddress(ip.AsSpan(8, 16)), sourcePort);
        _mux.Inject(ip.AsMemory(48, length), source, viaRelay: false);
    }

    private async Task TimerLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_server == null && Environment.TickCount64 - _lastResolve >= ResolveRetryMs)
                {
                    _lastResolve = Environment.TickCount64;
                    if (await TryResolveServerAsync(_cts.Token).ConfigureAwait(false))
                        lock (_gate) SendInitiationLocked();
                }
                bool live;
                lock (_gate)
                {
                    long now = Environment.TickCount64;
                    live = HasLiveSessionLocked(out var session);
                    if (!live) MaybeInitiateLocked();
                    else
                    {
                        if (now - session.CreatedAt >= RekeyAfterMs) MaybeInitiateLocked();
                        long interval = (_config.PersistentKeepalive > 0 ? _config.PersistentKeepalive : 25) * 1000L;
                        if (now - _lastSend >= interval) SendDataLocked(session, Array.Empty<byte>());
                    }
                }
                SetState(live ? VpnState.Connected : VpnState.Connecting);
                bool silent = live && SilentFor > SilentWarning;
                if (silent && !_silentLogged)
                    _log?.Invoke($"VPN: nothing has come back from {_config.Endpoint} for {(int)SilentFor.TotalSeconds}s. If another device uses the same WireGuard configuration, only one of them can be connected; give each device its own.");
                _silentLogged = silent;
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetState(VpnState state)
    {
        if (_cts.IsCancellationRequested && state != VpnState.Disconnected) return;
        VpnState previous;
        lock (_gate)
        {
            previous = _state;
            _state = state;
        }
        if (previous != state) StateChanged?.Invoke(state);
    }

    public string Describe() => _state switch
    {
        VpnState.Connected => $"VPN connected ({_config.Endpoint})",
        VpnState.Connecting => $"VPN connecting to {_config.Endpoint}…",
        _ => "VPN off",
    };

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _socket?.Dispose(); } catch (ObjectDisposedException) { }
        SetState(VpnState.Disconnected);
    }
}
