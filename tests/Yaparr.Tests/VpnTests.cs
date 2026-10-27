using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Yaparr.Transport;
using Yaparr.Vpn;

namespace Yaparr.Tests;

internal sealed class TestWireGuardServer : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _privateKey = WireGuardCrypto.GeneratePrivateKey();
    private readonly byte[]? _psk;
    private byte[]? _sendKey, _receiveKey;
    private uint _localIndex, _remoteIndex;
    private IPEndPoint? _client;
    private long _counter;

    public TestWireGuardServer(byte[]? presharedKey = null)
    {
        _psk = presharedKey;
        PublicKey = WireGuardCrypto.PublicKey(_privateKey);
        _ = Task.Run(RunAsync);
    }

    public byte[] PublicKey { get; }
    public IPEndPoint EndPoint => (IPEndPoint)_socket.Client.LocalEndPoint!;
    public int Handshakes;
    public bool DropData;
    public List<(IPEndPoint To, byte[] Payload, int SourcePort)> Received { get; } = new();
    public Func<IPEndPoint, byte[], byte[]?>? Reply { get; set; }

    private UdpClient? _nat;
    private int _clientPort;
    private IPAddress? _clientAddress;

    public IPEndPoint EnableForwarding()
    {
        _nat = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await _nat.ReceiveAsync(_cts.Token);
                    if (_clientAddress != null && _sendKey != null) await SendIp(r.RemoteEndPoint, _clientPort, _clientAddress, r.Buffer);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        });
        return (IPEndPoint)_nat.Client.LocalEndPoint!;
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var r = await _socket.ReceiveAsync(_cts.Token);
                var m = r.Buffer;
                if (m.Length == 148 && m[0] == 1) Respond(m, r.RemoteEndPoint);
                else if (m.Length >= 32 && m[0] == 4 && !DropData) await HandleData(m);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private void Respond(byte[] m, IPEndPoint from)
    {
        var mac1Key = WireGuardCrypto.Hash(WireGuardCrypto.LabelMac1, PublicKey);
        if (!WireGuardCrypto.Mac16(mac1Key, m[..116]).SequenceEqual(m[116..132])) return;

        var c = WireGuardCrypto.Hash(WireGuardCrypto.Construction);
        var h = WireGuardCrypto.Hash(c, WireGuardCrypto.Identifier);
        h = WireGuardCrypto.Hash(h, PublicKey);

        var ephemeralI = m[8..40];
        c = WireGuardCrypto.Kdf1(c, ephemeralI);
        h = WireGuardCrypto.Hash(h, ephemeralI);
        var (c1, k1) = WireGuardCrypto.Kdf2(c, WireGuardCrypto.Dh(_privateKey, ephemeralI));
        var staticI = WireGuardCrypto.Open(k1, 0, m.AsSpan(40, 48), h);
        if (staticI == null) return;
        h = WireGuardCrypto.Hash(h, m[40..88]);
        var (c2, k2) = WireGuardCrypto.Kdf2(c1, WireGuardCrypto.Dh(_privateKey, staticI));
        if (WireGuardCrypto.Open(k2, 0, m.AsSpan(88, 28), h) == null) return;
        h = WireGuardCrypto.Hash(h, m[88..116]);

        var ephemeralPrivate = WireGuardCrypto.GeneratePrivateKey();
        var ephemeralPublic = WireGuardCrypto.PublicKey(ephemeralPrivate);
        c = WireGuardCrypto.Kdf1(c2, ephemeralPublic);
        h = WireGuardCrypto.Hash(h, ephemeralPublic);
        c = WireGuardCrypto.Kdf1(c, WireGuardCrypto.Dh(ephemeralPrivate, ephemeralI));
        c = WireGuardCrypto.Kdf1(c, WireGuardCrypto.Dh(ephemeralPrivate, staticI));
        var (c3, tau, k3) = WireGuardCrypto.Kdf3(c, _psk ?? new byte[32]);
        h = WireGuardCrypto.Hash(h, tau);
        var empty = WireGuardCrypto.Seal(k3, 0, Array.Empty<byte>(), h);

        var (receiveKey, sendKey) = WireGuardCrypto.Kdf2(c3, Array.Empty<byte>());
        _receiveKey = receiveKey;
        _sendKey = sendKey;
        _remoteIndex = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4));
        _localIndex = (uint)Random.Shared.Next(1, int.MaxValue);
        _client = from;
        Interlocked.Increment(ref Handshakes);

        var response = new byte[92];
        response[0] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), _localIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(8), _remoteIndex);
        ephemeralPublic.CopyTo(response, 12);
        empty.CopyTo(response, 44);
        var clientMac1Key = WireGuardCrypto.Hash(WireGuardCrypto.LabelMac1, staticI);
        WireGuardCrypto.Mac16(clientMac1Key, response[..60]).CopyTo(response, 60);
        _socket.Send(response, response.Length, from);
    }

    private async Task HandleData(byte[] m)
    {
        if (_receiveKey == null || BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4)) != _localIndex) return;
        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(m.AsSpan(8));
        var plain = WireGuardCrypto.Open(_receiveKey, counter, m.AsSpan(16), Array.Empty<byte>());
        if (plain == null || plain.Length < 28) return;

        int total = BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(2));
        var to = new IPEndPoint(new IPAddress(plain.AsSpan(16, 4)), BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(22)));
        int sourcePort = BinaryPrimitives.ReadUInt16BigEndian(plain.AsSpan(20));
        var payload = plain[28..total];
        lock (Received) Received.Add((to, payload, sourcePort));
        _clientPort = sourcePort;
        _clientAddress = new IPAddress(plain.AsSpan(12, 4));
        if (_nat != null) await _nat.SendAsync(payload, payload.Length, to);

        if (Reply?.Invoke(to, payload) is { } answer)
            await SendIp(to, sourcePort, new IPAddress(plain.AsSpan(12, 4)), answer);
    }

    private async Task SendIp(IPEndPoint from, int toPort, IPAddress toAddress, byte[] payload)
    {
        var ip = new byte[28 + payload.Length];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        ip[8] = 64;
        ip[9] = 17;
        from.Address.GetAddressBytes().CopyTo(ip, 12);
        toAddress.GetAddressBytes().CopyTo(ip, 16);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(20), (ushort)from.Port);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(22), (ushort)toPort);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(24), (ushort)(8 + payload.Length));
        payload.CopyTo(ip, 28);

        int padded = (ip.Length + 15) & ~15;
        var body = new byte[padded];
        ip.CopyTo(body, 0);
        ulong counter = (ulong)Interlocked.Increment(ref _counter) - 1;
        var sealedBody = WireGuardCrypto.Seal(_sendKey!, counter, body, Array.Empty<byte>());
        var message = new byte[16 + sealedBody.Length];
        message[0] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), _remoteIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(8), counter);
        sealedBody.CopyTo(message, 16);
        await _socket.SendAsync(message, message.Length, _client!);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _nat?.Dispose();
    }
}

public class WireGuardCryptoTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void Blake2s_and_its_hmac_match_reference_values()
    {
        Assert.Equal(Hex("508c5e8c327c14e2e1a72ba34eeb452f37458b209ed63a294d999b4c86675982"), WireGuardCrypto.Hash("abc"u8.ToArray()));
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(Hex("4ef3f678fd2c65d0c7b0cc82b8bcf382e756335d2b72658311ce291c5e8efc82"), WireGuardCrypto.Hmac(key, "wireguard test"u8.ToArray()));
        var longKey = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        Assert.Equal(Hex("096d2dec44b647196383c466daf27592cf0c187793f83d1e9f8a19efc137d0d1"), WireGuardCrypto.Hmac(longKey, "long key"u8.ToArray()));
    }

    [Fact]
    public void X25519_matches_rfc7748()
    {
        var alice = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bobPublic = Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        Assert.Equal(Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"), WireGuardCrypto.PublicKey(alice));
        Assert.Equal(Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"), WireGuardCrypto.Dh(alice, bobPublic));
    }

    [Fact]
    public void Aead_round_trips_and_rejects_tampering()
    {
        var key = WireGuardCrypto.GeneratePrivateKey();
        var sealedData = WireGuardCrypto.Seal(key, 7, "hello"u8.ToArray(), "aad"u8.ToArray());
        Assert.Equal("hello"u8.ToArray(), WireGuardCrypto.Open(key, 7, sealedData, "aad"u8.ToArray()));
        Assert.Null(WireGuardCrypto.Open(key, 8, sealedData, "aad"u8.ToArray()));
        Assert.Null(WireGuardCrypto.Open(key, 7, sealedData, "other"u8.ToArray()));
        sealedData[0] ^= 1;
        Assert.Null(WireGuardCrypto.Open(key, 7, sealedData, "aad"u8.ToArray()));
    }

    [Fact]
    public void Replay_window_rejects_repeats_and_very_old_counters()
    {
        var w = new ReplayWindow();
        Assert.True(w.Accept(5));
        Assert.False(w.Accept(5));
        Assert.True(w.Accept(3));
        Assert.True(w.Accept(10));
        Assert.False(w.Accept(3));
        Assert.True(w.Accept(5000));
        Assert.False(w.Accept(10));
        Assert.True(w.Accept(4999));
        Assert.False(w.Accept(4999));
    }
}

public class WireGuardConfigTests
{
    private static string Key(int seed) => Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + seed)).ToArray());

    [Fact]
    public void A_standard_config_file_is_parsed()
    {
        var config = WireGuardConfig.Parse($"""
            # exported by a provider
            [Interface]
            PrivateKey = {Key(1)}
            Address = 10.2.0.2/32, fd00::2/128
            DNS = 10.2.0.1
            MTU = 1380

            [Peer]
            PublicKey = {Key(2)}
            PresharedKey = {Key(3)}
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = vpn.example.com:51820
            PersistentKeepalive = 25
            """);

        Assert.Equal(IPAddress.Parse("10.2.0.2"), config.Address);
        Assert.Equal(1380, config.Mtu);
        Assert.Equal(25, config.PersistentKeepalive);
        Assert.Equal("vpn.example.com:51820", config.Endpoint);
        Assert.Equal(32, config.PresharedKey!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[Interface]\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = x\nEndpoint = a:1")]
    [InlineData("garbage")]
    public void Bad_files_are_rejected_with_a_message(string text)
    {
        Assert.Throws<FormatException>(() => WireGuardConfig.Parse(text));
    }

    [Fact]
    public void Keys_must_be_32_bytes_of_base64()
    {
        var text = $"[Interface]\nPrivateKey = AAAA\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = {Key(2)}\nEndpoint = a.example:1";
        var ex = Assert.Throws<FormatException>(() => WireGuardConfig.Parse(text));
        Assert.Contains("PrivateKey", ex.Message);
    }
}

public class WireGuardTunnelTests
{
    private static WireGuardConfig ConfigFor(TestWireGuardServer server, byte[]? psk = null) => new()
    {
        PrivateKey = WireGuardCrypto.GeneratePrivateKey(),
        Address = IPAddress.Parse("10.8.0.2"),
        PeerPublicKey = server.PublicKey,
        PresharedKey = psk,
        Endpoint = server.EndPoint.ToString(),
        PersistentKeepalive = 1,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handshake_completes_and_udp_flows_both_ways(bool preshared)
    {
        var psk = preshared ? WireGuardCrypto.GeneratePrivateKey() : null;
        using var server = new TestWireGuardServer(psk);
        server.Reply = (_, payload) => payload.Reverse().ToArray();
        using var mux = new UdpMux();
        var received = new TaskCompletionSource<(byte[] Data, IPEndPoint From)>();
        mux.AddHandler((p, from) => { received.TrySetResult((p.ToArray(), from)); return true; });
        mux.Start();
        using var tunnel = new WireGuardTunnel(ConfigFor(server, psk), mux);
        mux.Tunnel = tunnel;

        Assert.True(await tunnel.ConnectAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(VpnState.Connected, tunnel.State);

        var remote = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 6881);
        mux.Send(new byte[] { 1, 2, 3, 4, 5 }, remote);

        var (data, from) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 5, 4, 3, 2, 1 }, data);
        Assert.Equal(remote, from);

        lock (server.Received)
        {
            var seen = Assert.Single(server.Received, r => r.Payload.Length == 5);
            Assert.Equal(remote, seen.To);
            Assert.Equal(mux.LocalPort, seen.SourcePort);
        }
    }

    [Fact]
    public async Task Traffic_to_local_addresses_bypasses_the_tunnel()
    {
        using var server = new TestWireGuardServer();
        using var mux = new UdpMux();
        mux.Start();
        using var tunnel = new WireGuardTunnel(ConfigFor(server), mux);
        mux.Tunnel = tunnel;
        Assert.True(await tunnel.ConnectAsync(TimeSpan.FromSeconds(5)));

        using var lan = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        mux.Send(new byte[] { 9, 9 }, (IPEndPoint)lan.Client.LocalEndPoint!);
        var got = await lan.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 9, 9 }, got.Buffer);

        lock (server.Received) Assert.DoesNotContain(server.Received, r => r.Payload.SequenceEqual(new byte[] { 9, 9 }));
    }

    [Fact]
    public async Task Public_traffic_is_dropped_not_leaked_while_the_tunnel_is_down()
    {
        var server = new TestWireGuardServer();
        var config = ConfigFor(server);
        server.Dispose();
        using var mux = new UdpMux();
        mux.Start();
        using var tunnel = new WireGuardTunnel(config, mux);
        mux.Tunnel = tunnel;

        Assert.False(await tunnel.ConnectAsync(TimeSpan.FromMilliseconds(500)));
        Assert.NotEqual(VpnState.Connected, tunnel.State);
        Assert.True(tunnel.TrySend(new byte[] { 1 }, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 53)));
    }

    [Fact]
    public async Task A_server_with_the_wrong_key_never_completes_the_handshake()
    {
        using var server = new TestWireGuardServer();
        using var other = new TestWireGuardServer();
        using var mux = new UdpMux();
        mux.Start();
        var config = new WireGuardConfig
        {
            PrivateKey = WireGuardCrypto.GeneratePrivateKey(),
            Address = IPAddress.Parse("10.8.0.2"),
            PeerPublicKey = other.PublicKey,
            Endpoint = server.EndPoint.ToString(),
        };
        using var tunnel = new WireGuardTunnel(config, mux);
        Assert.False(await tunnel.ConnectAsync(TimeSpan.FromMilliseconds(800)));
        Assert.Equal(0, server.Handshakes);
    }

    [Fact]
    public async Task Stun_through_the_tunnel_reports_the_servers_view_of_the_network()
    {
        using var server = new TestWireGuardServer();
        server.Reply = (to, payload) =>
        {
            var request = StunMessage.Decode(payload);
            if (request == null) return null;
            return new StunMessage(StunType.BindingSuccess, request.TransactionId)
                .AddXorAddress(StunAttr.XorMappedAddress, new IPEndPoint(IPAddress.Parse("198.51.100.9"), 41000)).Encode();
        };
        using var mux = new UdpMux();
        mux.Start();
        using var tunnel = new WireGuardTunnel(ConfigFor(server), mux);
        mux.Tunnel = tunnel;
        Assert.True(await tunnel.ConnectAsync(TimeSpan.FromSeconds(5)));

        var report = await NatProbe.RunAsync(mux, new[] { "203.0.113.7:3478", "203.0.113.8:3478" });
        Assert.Equal(NatType.EndpointIndependent, report.Type);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.9"), 41000), report.PublicEndPoint);
    }
}

internal sealed class FixedDiscovery : Yaparr.Discovery.IPeerDiscovery
{
    private readonly Func<IReadOnlyList<IPEndPoint>> _peers;
    public FixedDiscovery(Func<IReadOnlyList<IPEndPoint>> peers) => _peers = peers;
    public Task AnnounceAsync(byte[] topic, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default) => Task.FromResult(_peers());
    public void Dispose() { }
}

public class VpnChatTests
{
    [Fact]
    public async Task A_user_behind_the_vpn_and_a_direct_user_can_chat_in_both_directions()
    {
        using var server = new TestWireGuardServer();
        var natEndPoint = server.EnableForwarding();

        IPEndPoint? bEndPoint = null;
        IPEndPoint? aEndPoint = null;
        using var a = new Yaparr.Chat.ChatNode(new Yaparr.Chat.ChatNodeOptions
        {
            DisplayName = "vpn-user",
            DiscoveryFactory = _ => new FixedDiscovery(() => bEndPoint == null ? Array.Empty<IPEndPoint>() : new[] { bEndPoint }),
            DiscoveryInterval = TimeSpan.FromSeconds(1),
        });
        using var b = new Yaparr.Chat.ChatNode(new Yaparr.Chat.ChatNodeOptions
        {
            DisplayName = "direct-user",
            DiscoveryFactory = _ => new FixedDiscovery(() => aEndPoint == null ? Array.Empty<IPEndPoint>() : new[] { aEndPoint }),
            DiscoveryInterval = TimeSpan.FromSeconds(1),
        });

        var tunnel = new WireGuardTunnel(new WireGuardConfig
        {
            PrivateKey = WireGuardCrypto.GeneratePrivateKey(),
            Address = IPAddress.Parse("10.8.0.2"),
            PeerPublicKey = server.PublicKey,
            Endpoint = server.EndPoint.ToString(),
            PersistentKeepalive = 1,
        }, a.Mux) { TunnelLocalAddresses = true };
        a.Mux.Tunnel = tunnel;
        Assert.True(await tunnel.ConnectAsync(TimeSpan.FromSeconds(5)));

        a.Start();
        b.Start();
        bEndPoint = new IPEndPoint(IPAddress.Loopback, b.LocalPort);
        aEndPoint = natEndPoint;

        var key = Yaparr.Chat.RoomKey.Generate();
        var ra = await a.JoinPrivateRoomAsync(key, "vpn");
        var rb = await b.JoinPrivateRoomAsync(key, "vpn");
        var gotB = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var gotA = new System.Collections.Concurrent.ConcurrentQueue<string>();
        rb.MessageReceived += m => gotB.Enqueue(m.Text);
        ra.MessageReceived += m => gotA.Enqueue(m.Text);

        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1, timeoutMs: 20000);

        await ra.SendAsync("from vpn");
        await rb.SendAsync("from direct");
        await TestUtil.WaitFor(() => gotB.Contains("from vpn") && gotA.Contains("from direct"), timeoutMs: 10000);
        tunnel.Dispose();
    }
}
