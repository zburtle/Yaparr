using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Yaparr.Chat;
using Yaparr.Discovery.Dht;
using Yaparr.Transport;

namespace Yaparr.Tests;

internal sealed class FakeStunServer : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint? _report;
    private readonly CancellationTokenSource _cts = new();

    public FakeStunServer(IPEndPoint? report = null)
    {
        _report = report;
        _ = Task.Run(RunAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_socket.Client.LocalEndPoint!;

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var r = await _socket.ReceiveAsync(_cts.Token);
                var request = StunMessage.Decode(r.Buffer);
                if (request is not { Type: StunType.BindingRequest }) continue;
                var reply = new StunMessage(StunType.BindingSuccess, request.TransactionId)
                    .AddXorAddress(StunAttr.XorMappedAddress, _report ?? r.RemoteEndPoint);
                await _socket.SendAsync(reply.Encode(), r.RemoteEndPoint);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
    }
}

internal sealed class FakeTurnServer : IDisposable
{
    private const string Realm = "yaparr.test";
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly UdpClient _relay = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _key;
    private readonly string _user;
    private IPEndPoint? _client;

    public FakeTurnServer(string user, string password)
    {
        _user = user;
        _key = StunMessage.LongTermKey(user, Realm, password);
        _ = Task.Run(ServeAsync);
        _ = Task.Run(RelayAsync);
    }

    public IPEndPoint EndPoint => (IPEndPoint)_socket.Client.LocalEndPoint!;
    public IPEndPoint RelayEndPoint => (IPEndPoint)_relay.Client.LocalEndPoint!;
    public HashSet<IPAddress> Permitted { get; } = new();

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var r = await _socket.ReceiveAsync(_cts.Token);
                var msg = StunMessage.Decode(r.Buffer);
                if (msg == null) continue;

                if (msg.Type == StunType.SendIndication)
                {
                    var peer = msg.GetXorAddress(StunAttr.XorPeerAddress);
                    var data = msg.Get(StunAttr.Data);
                    bool allowed;
                    lock (Permitted) allowed = peer != null && Permitted.Contains(peer.Address);
                    if (allowed && data != null) await _relay.SendAsync(data, peer!);
                    continue;
                }

                var failure = (ushort)(msg.Type | 0x0110);
                if (msg.GetString(StunAttr.Username) != _user || !StunMessage.VerifyIntegrity(r.Buffer, _key))
                {
                    var error = new StunMessage(failure, msg.TransactionId)
                        .Add(StunAttr.ErrorCode, StunMessage.ErrorValue(401, "Unauthorized"))
                        .Add(StunAttr.Realm, Realm)
                        .Add(StunAttr.Nonce, "nonce-1");
                    await _socket.SendAsync(error.Encode(), r.RemoteEndPoint);
                    continue;
                }

                var ok = new StunMessage((ushort)(msg.Type | 0x0100), msg.TransactionId);
                switch (msg.Type)
                {
                    case StunType.AllocateRequest:
                        _client = r.RemoteEndPoint;
                        ok.AddXorAddress(StunAttr.XorRelayedAddress, RelayEndPoint)
                          .AddXorAddress(StunAttr.XorMappedAddress, r.RemoteEndPoint)
                          .Add(StunAttr.Lifetime, new byte[] { 0, 0, 2, 0x58 });
                        break;
                    case StunType.CreatePermissionRequest:
                        if (msg.GetXorAddress(StunAttr.XorPeerAddress) is { } peer2)
                            lock (Permitted) Permitted.Add(peer2.Address);
                        break;
                    case StunType.RefreshRequest:
                        ok.Add(StunAttr.Lifetime, new byte[] { 0, 0, 2, 0x58 });
                        break;
                }
                await _socket.SendAsync(ok.Encode(), r.RemoteEndPoint);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task RelayAsync()
    {
        try
        {
            while (true)
            {
                var r = await _relay.ReceiveAsync(_cts.Token);
                bool allowed;
                lock (Permitted) allowed = Permitted.Contains(r.RemoteEndPoint.Address);
                if (!allowed || _client == null) continue;
                var indication = new StunMessage(StunType.DataIndication)
                    .AddXorAddress(StunAttr.XorPeerAddress, r.RemoteEndPoint)
                    .Add(StunAttr.Data, r.Buffer);
                await _socket.SendAsync(indication.Encode(), _client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _socket.Dispose();
        _relay.Dispose();
    }
}

public class StunTests
{
    [Fact]
    public void Messages_round_trip_with_addresses_and_integrity()
    {
        var key = StunMessage.LongTermKey("alice", "realm", "secret");
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 54321);
        var message = new StunMessage(StunType.BindingRequest)
            .AddXorAddress(StunAttr.XorMappedAddress, endpoint)
            .Add(StunAttr.Username, "alice");

        var bytes = message.Encode(key);
        Assert.True(StunMessage.LooksLikeStun(bytes));
        Assert.Equal(0, bytes.Length % 4);

        var decoded = StunMessage.Decode(bytes)!;
        Assert.Equal(message.TransactionId, decoded.TransactionId);
        Assert.Equal(endpoint, decoded.GetMappedAddress());
        Assert.Equal("alice", decoded.GetString(StunAttr.Username));
        Assert.True(StunMessage.VerifyIntegrity(bytes, key));
        Assert.False(StunMessage.VerifyIntegrity(bytes, StunMessage.LongTermKey("alice", "realm", "other")));

        bytes[24] ^= 1;
        Assert.False(StunMessage.VerifyIntegrity(bytes, key));
    }

    [Fact]
    public void Packets_from_other_protocols_are_not_mistaken_for_stun()
    {
        Assert.False(StunMessage.LooksLikeStun(Bencode.Encode(new BDict { ["y"] = "q" })));
        Assert.False(StunMessage.LooksLikeStun(new byte[] { 0xA7, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Nat_type_is_classified_from_the_mappings()
    {
        var a = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 4000);
        var b = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 4001);
        var none = Array.Empty<IPAddress>();

        Assert.Equal(NatType.Unknown, NatProbe.Classify(Array.Empty<IPEndPoint>(), none).Type);
        Assert.Equal(NatType.Unknown, NatProbe.Classify(new[] { a }, none).Type);
        Assert.Equal(NatType.EndpointIndependent, NatProbe.Classify(new[] { a, a }, none).Type);
        Assert.Equal(NatType.Symmetric, NatProbe.Classify(new[] { a, b }, none).Type);
        Assert.Equal(NatType.Open, NatProbe.Classify(new[] { a, a }, new[] { a.Address }).Type);
    }

    [Fact]
    public async Task Probe_reports_the_public_address_and_detects_symmetric_mapping()
    {
        using var s1 = new FakeStunServer(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 4000));
        using var s2 = new FakeStunServer(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 4000));
        using var s3 = new FakeStunServer(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 4999));
        using var mux = new UdpMux();
        mux.Start();

        var cone = await NatProbe.RunAsync(mux, new[] { s1.EndPoint.ToString(), s2.EndPoint.ToString() });
        Assert.Equal(NatType.EndpointIndependent, cone.Type);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.5"), 4000), cone.PublicEndPoint);

        var symmetric = await NatProbe.RunAsync(mux, new[] { s1.EndPoint.ToString(), s3.EndPoint.ToString() });
        Assert.Equal(NatType.Symmetric, symmetric.Type);
    }

    [Fact]
    public async Task Probe_without_reachable_servers_is_unknown()
    {
        using var dead = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var mux = new UdpMux();
        mux.Start();
        var report = await NatProbe.RunAsync(mux, new[] { dead.Client.LocalEndPoint!.ToString()! });
        Assert.Equal(NatType.Unknown, report.Type);
        Assert.Null(report.PublicEndPoint);
    }
}

public class TurnTests
{
    private static TurnClient Client(UdpMux mux, FakeTurnServer server, string password = "pw") =>
        new(mux, new TurnOptions { Server = server.EndPoint.ToString(), Username = "u", Credential = password }) { RelayLocalAddresses = true };

    [Fact]
    public async Task Allocation_authenticates_and_returns_a_relayed_address()
    {
        using var server = new FakeTurnServer("u", "pw");
        using var mux = new UdpMux();
        mux.Start();
        using var turn = Client(mux, server);

        Assert.True(await turn.AllocateAsync());
        Assert.Equal(server.RelayEndPoint, turn.RelayedEndPoint);
        Assert.True(turn.IsActive);
    }

    [Fact]
    public async Task Allocation_with_the_wrong_credential_fails()
    {
        using var server = new FakeTurnServer("u", "pw");
        using var mux = new UdpMux();
        mux.Start();
        using var turn = Client(mux, server, password: "wrong");

        Assert.False(await turn.AllocateAsync());
        Assert.False(turn.IsActive);
    }

    [Fact]
    public async Task Packets_flow_both_ways_through_the_relay_once_permission_exists()
    {
        using var server = new FakeTurnServer("u", "pw");
        using var mux = new UdpMux();
        var received = new TaskCompletionSource<(byte[] Data, IPEndPoint From)>();
        mux.AddHandler((p, from) => { if (p.Span[0] == 0xA7) received.TrySetResult((p.ToArray(), from)); return p.Span[0] == 0xA7; });
        mux.Start();
        using var turn = Client(mux, server);
        Assert.True(await turn.AllocateAsync());

        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var peerEndPoint = (IPEndPoint)peer.Client.LocalEndPoint!;

        Assert.True(await turn.EnsurePermissionAsync(peerEndPoint.Address));
        await peer.SendAsync(new byte[] { 0xA7, 2, 9 }, turn.RelayedEndPoint!);
        var (data, from) = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 0xA7, 2, 9 }, data);
        Assert.Equal(peerEndPoint, from);

        mux.SendRelayed(new byte[] { 0xA7, 2, 7 }, peerEndPoint);
        var reply = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 0xA7, 2, 7 }, reply.Buffer);
        Assert.Equal(turn.RelayedEndPoint, reply.RemoteEndPoint);
    }

    [Fact]
    public async Task Hellos_go_both_ways_and_data_follows_whichever_path_is_working()
    {
        using var server = new FakeTurnServer("u", "pw");
        using var mux = new UdpMux();
        mux.Start();
        using var turn = Client(mux, server);
        Assert.True(await turn.AllocateAsync());

        var peer = new IPEndPoint(IPAddress.Loopback, 5555);
        var hello = new byte[] { 0xA7, 1, 1 };
        var data = new byte[] { 0xA7, 2, 1 };

        Assert.Equal(RouteMode.Both, turn.Choose(peer, hello));
        Assert.Equal(RouteMode.Direct, turn.Choose(peer, data));
        Assert.Equal(RouteMode.Direct, turn.Choose(peer, Bencode.Encode(new BDict { ["y"] = "q" })));

        turn.NoteReceived(peer, viaRelay: true);
        Assert.Equal(RouteMode.Relay, turn.Choose(peer, data));

        turn.NoteReceived(peer, viaRelay: false);
        Assert.Equal(RouteMode.Direct, turn.Choose(peer, data));
    }

    [Fact]
    public async Task Dht_announce_through_the_relay_publishes_the_relayed_address()
    {
        using var turnServer = new FakeTurnServer("u", "pw");
        using var dhtMux = new UdpMux();
        using var dht = new DhtNode(dhtMux, new DhtOptions { ReadOnly = false, BootstrapHosts = Array.Empty<string>() });
        dhtMux.Start();

        var options = new DhtOptions { BootstrapHosts = new[] { $"127.0.0.1:{dhtMux.LocalPort}" } };
        using var aMux = new UdpMux();
        using var a = new DhtNode(aMux, options);
        aMux.Start();
        using var turn = Client(aMux, turnServer);
        Assert.True(await turn.AllocateAsync());

        using var bMux = new UdpMux();
        using var b = new DhtNode(bMux, options);
        bMux.Start();

        var topic = System.Security.Cryptography.SHA1.HashData("relay-topic"u8);
        Assert.True(await a.AnnounceAsync(topic) >= 2);

        var found = await b.GetPeersAsync(topic);
        Assert.Contains(turn.RelayedEndPoint!, found);
        Assert.Contains(new IPEndPoint(IPAddress.Loopback, aMux.LocalPort), found);
    }
}

public class PortMapperTests
{
    [Fact]
    public async Task Nat_pmp_maps_the_port_and_reports_the_external_address()
    {
        using var gateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var gatewayPort = ((IPEndPoint)gateway.Client.LocalEndPoint!).Port;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await gateway.ReceiveAsync();
                    var reply = new byte[r.Buffer[1] == 0 ? 12 : 16];
                    reply[1] = (byte)(128 + r.Buffer[1]);
                    if (r.Buffer[1] == 0)
                    {
                        IPAddress.Parse("203.0.113.9").GetAddressBytes().CopyTo(reply, 8);
                    }
                    else
                    {
                        r.Buffer.AsSpan(4, 2).CopyTo(reply.AsSpan(8));
                        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), 40000);
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), 3600);
                    }
                    await gateway.SendAsync(reply, r.RemoteEndPoint);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });

        using var mapper = new PortMapper(12345, new PortMapperOptions
        {
            Gateway = IPAddress.Loopback,
            NatPmpPort = gatewayPort,
            Timeout = TimeSpan.FromSeconds(1),
        });
        var mapping = await mapper.MapAsync();

        Assert.NotNull(mapping);
        Assert.Equal("NAT-PMP", mapping!.Method);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000), mapping.ExternalEndPoint);
        gateway.Dispose();
        await serve;
    }

    [Fact]
    public void Upnp_device_descriptions_are_parsed_for_the_wan_service()
    {
        const string xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device><deviceList><device><deviceList><device>
                <serviceList>
                  <service><serviceType>urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1</serviceType><controlURL>/common</controlURL></service>
                  <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/IPConn</controlURL></service>
                </serviceList>
              </device></deviceList></device></deviceList></device>
            </root>
            """;
        Assert.True(PortMapper.ParseDescription(xml, "http://192.168.1.1:5000/rootDesc.xml", out var control, out var service));
        Assert.Equal("http://192.168.1.1:5000/ctl/IPConn", control);
        Assert.Contains("WANIPConnection", service);
    }

    [Fact]
    public async Task Upnp_discovers_the_gateway_and_adds_a_mapping()
    {
        using var http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        int httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
        var actions = new List<string>();
        var httpTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await http.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    var buffer = new byte[8192];
                    int total = 0;
                    string text = "";
                    while (!(text.Contains("\r\n\r\n") && (!text.Contains("POST") || text.Contains("</s:Envelope>"))))
                    {
                        int n = await stream.ReadAsync(buffer.AsMemory(total));
                        if (n == 0) break;
                        total += n;
                        text = Encoding.UTF8.GetString(buffer, 0, total);
                    }

                    string body;
                    if (text.StartsWith("GET"))
                    {
                        body = $"""<root xmlns="urn:schemas-upnp-org:device-1-0"><device><serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl</controlURL></service></serviceList></device></root>""";
                    }
                    else if (text.Contains("GetExternalIPAddress") && !text.Contains("AddPortMapping"))
                    {
                        actions.Add("ip");
                        body = """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:GetExternalIPAddressResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1"><NewExternalIPAddress>203.0.113.20</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>""";
                    }
                    else
                    {
                        actions.Add("add");
                        body = """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:AddPortMappingResponse xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1"/></s:Body></s:Envelope>""";
                    }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/xml\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(bytes);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            catch (InvalidOperationException) { }
        });

        using var ssdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var ssdpEndPoint = (IPEndPoint)ssdp.Client.LocalEndPoint!;
        var ssdpTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var r = await ssdp.ReceiveAsync();
                    var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nLOCATION: http://127.0.0.1:{httpPort}/desc.xml\r\nST: upnp:rootdevice\r\n\r\n");
                    await ssdp.SendAsync(reply, r.RemoteEndPoint);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        });

        using var dead = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var mapper = new PortMapper(23456, new PortMapperOptions
        {
            Gateway = IPAddress.Loopback,
            NatPmpPort = ((IPEndPoint)dead.Client.LocalEndPoint!).Port,
            SsdpEndPoint = ssdpEndPoint,
            Timeout = TimeSpan.FromSeconds(1),
        });
        var mapping = await mapper.MapAsync();

        Assert.NotNull(mapping);
        Assert.Equal("UPnP", mapping!.Method);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.20"), 23456), mapping.ExternalEndPoint);
        Assert.Contains("add", actions);

        http.Stop();
        ssdp.Dispose();
        await Task.WhenAll(httpTask, ssdpTask);
    }
}

public class TraversalSettingsTests
{
    [Fact]
    public void Settings_file_configures_stun_and_turn_and_bad_files_fall_back_to_defaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "network.json");
            Assert.True(TraversalOptions.Load(path).Enabled);

            File.WriteAllText(path, """{"portMapping":false,"stun":["stun.example.org:3478"],"turn":{"server":"turn.example.org:3478","username":"u","credential":"c","always":true}}""");
            var loaded = TraversalOptions.Load(path);
            Assert.False(loaded.PortMapping);
            Assert.Equal(new[] { "stun.example.org:3478" }, loaded.StunServers);
            Assert.Equal("turn.example.org:3478", loaded.Turn!.Server);
            Assert.True(loaded.Turn.Always);

            File.WriteAllText(path, "not json");
            var fallback = TraversalOptions.Load(path);
            Assert.True(fallback.Enabled);
            Assert.Null(fallback.Turn);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Turn_and_stun_urls_are_split_into_host_and_port()
    {
        Assert.Equal(("turn.example.org", 3478), TurnClient.SplitHostPort("turn:turn.example.org:3478?transport=udp", 1));
        Assert.Equal(("stun.example.org", 3478), TurnClient.SplitHostPort("stun.example.org", 3478));
        Assert.Equal(("a.b", 19302), TurnClient.SplitHostPort("a.b:19302", 3478));
    }
}
