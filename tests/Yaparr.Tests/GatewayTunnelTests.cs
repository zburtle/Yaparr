using System.Net;
using System.Threading.Channels;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Transport;
using Yaparr.Vpn;

namespace Yaparr.Tests;

/// <summary>A gateway link in memory: what the tunnel sends is kept, a test (or a reply function, such as a STUN server) puts frames in, and it can be closed.</summary>
internal sealed class FakeGatewayLink : IGatewayLink
{
    private readonly Channel<byte[]?> _incoming = Channel.CreateUnbounded<byte[]?>();
    public readonly List<byte[]> Sent = new();
    public Func<byte[], byte[]?>? Reply { get; set; }
    public bool FailToConnect { get; set; }
    public bool Disposed { get; private set; }

    public Task ConnectAsync(CancellationToken cancel) => FailToConnect ? throw new System.Net.WebSockets.WebSocketException("no") : Task.CompletedTask;

    public Task SendAsync(byte[] frame, CancellationToken cancel)
    {
        lock (Sent) Sent.Add(frame);
        if (Reply?.Invoke(frame) is { } reply) _incoming.Writer.TryWrite(reply);
        return Task.CompletedTask;
    }

    public async Task<byte[]?> ReceiveAsync(CancellationToken cancel) => await _incoming.Reader.ReadAsync(cancel);

    public void Put(byte[] frame) => _incoming.Writer.TryWrite(frame);

    public void Close() => _incoming.Writer.TryWrite(null);

    public void Dispose() => Disposed = true;
}

public sealed class GatewayFramesTests
{
    [Fact]
    public void Frames_are_the_bytes_the_service_expects()
    {
        // The same bytes as Yaparr.Cloud's own test of its frames: DATA, IPv4, port 6881 (0x1AE1), 203.0.113.9, a datagram of 1 2 3.
        var frame = GatewayFrames.EncodeData(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6881), new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 0x01, 0x04, 0x1A, 0xE1, 203, 0, 113, 9, 1, 2, 3 }, frame);
        Assert.True(GatewayFrames.TryDecode(frame, out var type, out var endpoint, out var start));
        Assert.Equal(GatewayFrames.Data, type);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.9"), 6881), endpoint);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame[start..]);

        var v6 = GatewayFrames.EncodeData(new IPEndPoint(IPAddress.Parse("2001:db8::7"), 1), new byte[] { 9 });
        Assert.Equal(6, v6[1]);
        Assert.Equal(4 + 16 + 1, v6.Length);
        Assert.Equal(4, GatewayFrames.EncodeData(new IPEndPoint(IPAddress.Parse("::ffff:203.0.113.9"), 1), new byte[] { 9 })[1]);
        Assert.False(GatewayFrames.TryDecode(new byte[] { 1, 4, 0, 1 }, out _, out _, out _));
        Assert.False(GatewayFrames.TryDecode(new byte[] { 0x11, 1, 0, 0, 0, 0, 0, 0 }, out _, out _, out _));
    }

    [Theory]
    [InlineData("203.0.113.9", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.20", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("224.0.0.251", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12::1", true)]
    [InlineData("::1", true)]
    public void Addresses_on_this_network_are_not_sent_through_the_service(string address, bool local)
    {
        Assert.Equal(local, GatewayFrames.IsLocal(IPAddress.Parse(address)));
    }
}

public sealed class GatewayTunnelTests
{
    private static readonly IPEndPoint Public = new(IPAddress.Parse("203.0.113.9"), 6881);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task Datagrams_to_public_addresses_go_through_the_link_and_what_comes_back_reaches_the_handlers()
    {
        using var mux = new UdpMux();
        mux.Start();
        var link = new FakeGatewayLink();
        using var tunnel = new GatewayTunnel(() => link, mux);
        mux.Tunnel = tunnel;
        var received = new List<(byte[] Packet, IPEndPoint From)>();
        mux.AddHandler((p, from) => { lock (received) received.Add((p.ToArray(), from)); return true; });
        Assert.True(await tunnel.ConnectAsync(TimeSpan.FromSeconds(5)));

        link.Put(GatewayFrames.EncodeData(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 4000), Array.Empty<byte>()).Select((b, i) => i == 0 ? GatewayFrames.Hello : b).ToArray());
        await WaitUntil(() => tunnel.PublicHint != null);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 4000), tunnel.PublicHint);

        mux.Send(new byte[] { 5, 6, 7 }, Public);
        await WaitUntil(() => { lock (link.Sent) return link.Sent.Count == 1; });
        Assert.Equal(GatewayFrames.EncodeData(Public, new byte[] { 5, 6, 7 }), link.Sent[0]);

        link.Put(GatewayFrames.EncodeData(Public, new byte[] { 8, 9 }));
        await WaitUntil(() => { lock (received) return received.Count == 1; });
        Assert.Equal(new byte[] { 8, 9 }, received[0].Packet);
        Assert.Equal(Public, received[0].From);
    }

    [Fact]
    public async Task Addresses_on_this_network_go_directly_and_a_datagram_that_is_too_big_is_dropped()
    {
        using var mux = new UdpMux();
        mux.Start();
        var link = new FakeGatewayLink();
        using var tunnel = new GatewayTunnel(() => link, mux);
        Assert.True(await tunnel.ConnectAsync(TimeSpan.FromSeconds(5)));

        Assert.False(tunnel.TrySend(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, 9)));          // the mux sends it itself
        Assert.True(tunnel.TrySend(new byte[GatewayFrames.MaxPacket + 1], Public));                     // taken, and dropped
        Assert.True(tunnel.TrySend(new byte[GatewayFrames.MaxPacket], Public));
        await WaitUntil(() => { lock (link.Sent) return link.Sent.Count == 1; });
        await Task.Delay(100);
        lock (link.Sent) Assert.Single(link.Sent);
    }

    [Fact]
    public async Task What_the_service_holds_back_is_told_and_a_link_that_closes_is_made_again()
    {
        using var mux = new UdpMux();
        mux.Start();
        var links = new List<FakeGatewayLink>();
        using var tunnel = new GatewayTunnel(() => { var l = new FakeGatewayLink(); links.Add(l); return l; }, mux);
        var limits = new List<byte>();
        tunnel.Limited += limits.Add;
        var states = new List<VpnState>();
        tunnel.StateChanged += states.Add;
        Assert.True(await tunnel.ConnectAsync(TimeSpan.FromSeconds(5)));

        links[0].Put(new[] { GatewayFrames.Limit, GatewayFrames.ReasonRate });
        await WaitUntil(() => limits.Count == 1);
        Assert.Equal(GatewayFrames.ReasonRate, limits[0]);

        links[0].Close();
        await WaitUntil(() => states.Contains(VpnState.Connecting));
        await WaitUntil(() => tunnel.State == VpnState.Connected && links.Count == 2);       // connected again, on a new link
        Assert.True(links[0].Disposed);
    }

    [Fact]
    public async Task A_link_that_cannot_connect_is_not_connected_and_disposing_ends_it()
    {
        using var mux = new UdpMux();
        mux.Start();
        var tunnel = new GatewayTunnel(() => new FakeGatewayLink { FailToConnect = true }, mux);
        Assert.False(await tunnel.ConnectAsync(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(VpnState.Connecting, tunnel.State);
        tunnel.Dispose();
        await WaitUntil(() => tunnel.State == VpnState.Disconnected);
        Assert.True(tunnel.TrySend(new byte[] { 1 }, Public));                                       // dropped, not sent directly
    }
}

public sealed class GatewayFallbackTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose() { foreach (var d in Enumerable.Reverse(_disposables)) d.Dispose(); }

    // A node whose STUN servers are public addresses nothing answers (a network that blocks UDP), so nothing comes back until something carries the datagrams.
    private ChatNode BlockedNode() => new(new ChatNodeOptions
    {
        DisplayName = "alice",
        EnableLocalDiscovery = false,
        Dht = new Yaparr.Discovery.Dht.DhtOptions { BootstrapHosts = Array.Empty<string>(), FallbackSeeds = Array.Empty<string>() },
        Traversal = new TraversalOptions
        {
            Enabled = true,
            PortMapping = false,
            LocalDiscovery = false,
            StunServers = new[] { "203.0.113.5:3478", "203.0.113.6:3478" },
        },
    });

    private static byte[]? StunAnswer(byte[] frame)
    {
        if (!GatewayFrames.TryDecode(frame, out _, out var to, out var start)) return null;
        var request = StunMessage.Decode(frame.AsSpan(start));
        if (request is not { Type: StunType.BindingRequest }) return null;
        var reply = new StunMessage(StunType.BindingSuccess, request.TransactionId)
            .AddXorAddress(StunAttr.XorMappedAddress, new IPEndPoint(IPAddress.Parse("198.51.100.7"), 4000)).Encode();
        return GatewayFrames.EncodeData(to, reply);
    }

    [Fact]
    public async Task A_network_that_blocks_udp_uses_the_gateway_and_then_has_an_address()
    {
        var node = BlockedNode();
        _disposables.Add(node);
        var link = new FakeGatewayLink { Reply = StunAnswer };
        var log = new List<string>();
        _ = new GatewayFallback(node, () => (new Uri("https://cloud.test"), "token"), () => true, log.Add, links: (_, _) => link);
        var blocked = new TaskCompletionSource();
        node.UdpBlocked += () => blocked.TrySetResult();

        node.Start();
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(20));
        for (int i = 0; i < 400 && node.NetworkStatus.Nat.PublicEndPoint == null; i++) await Task.Delay(50);

        Assert.Equal(VpnState.Connected, node.GatewayState);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.7"), 4000), node.NetworkStatus.Nat.PublicEndPoint);
        Assert.Contains("Yaparr Cloud gateway", node.NetworkStatus.Describe());
    }

    [Fact]
    public async Task The_connection_check_and_the_note_say_when_yaparr_cloud_is_acting_as_the_gateway()
    {
        Assert.Null(ChatNode.GatewayNote(VpnState.Disconnected));
        Assert.Contains("messages", ChatNode.GatewayNote(VpnState.Connected));
        Assert.Contains("Connecting", ChatNode.GatewayNote(VpnState.Connecting));

        var node = BlockedNode();
        _disposables.Add(node);
        var link = new FakeGatewayLink { Reply = StunAnswer };
        _ = new GatewayFallback(node, () => (new Uri("https://cloud.test"), "token"), () => true, null, links: (_, _) => link);
        node.Start();
        for (int i = 0; i < 600 && node.GatewayState != VpnState.Connected; i++) await Task.Delay(50);
        Assert.Equal(VpnState.Connected, node.GatewayState);

        var report = await node.CheckNetworkAsync();
        var item = Assert.Single(report.Items, i => i.Title == "Yaparr Cloud is acting as your gateway");
        Assert.Equal(CheckLevel.Ok, item.Level);
        Assert.Equal(ChatNode.GatewayNote(VpnState.Connected), item.Detail);
    }

    [Fact]
    public async Task A_gateway_that_gives_nothing_back_is_let_go_and_not_tried_again_for_a_while()
    {
        var node = BlockedNode();
        _disposables.Add(node);
        var links = new List<FakeGatewayLink>();
        var now = DateTimeOffset.UtcNow;
        var log = new List<string>();
        var fallback = new GatewayFallback(node, () => (new Uri("https://cloud.test"), "token"), () => true, log.Add, () => now,
            (_, _) => { var l = new FakeGatewayLink(); links.Add(l); return l; });          // connects, answers nothing
        node.Start();
        for (int i = 0; i < 600 && links.Count == 0; i++) await Task.Delay(50);
        Assert.NotEmpty(links);
        for (int i = 0; i < 600 && node.GatewayState != VpnState.Disconnected; i++) await Task.Delay(50);
        for (int i = 0; i < 600 && (fallback.Busy || log.Count == 0); i++) await Task.Delay(50);       // the attempt is over
        Assert.Equal(VpnState.Disconnected, node.GatewayState);
        int tried = links.Count;

        await fallback.TryAsync();                                  // inside the wait: nothing
        Assert.Equal(tried, links.Count);
        now += TimeSpan.FromMinutes(11);                            // after it
        await fallback.TryAsync();
        Assert.True(links.Count > tried, string.Join(" | ", log) + $" / state {node.GatewayState} / links {links.Count} tried {tried}");
    }

    [Fact]
    public async Task Nothing_happens_when_the_person_has_it_off_or_the_device_is_not_signed_in()
    {
        var node = BlockedNode();
        _disposables.Add(node);
        int made = 0;
        var off = new GatewayFallback(node, () => (new Uri("https://cloud.test"), "t"), () => false, null, links: (_, _) => { made++; return new FakeGatewayLink(); });
        await off.TryAsync();
        var signedOut = new GatewayFallback(node, () => null, () => true, null, links: (_, _) => { made++; return new FakeGatewayLink(); });
        await signedOut.TryAsync();
        Assert.Equal(0, made);
        Assert.Null(GatewayFallback.AccountOf(new BackupServerState()));
        Assert.Equal("https://cloud.test/", GatewayFallback.AccountOf(new BackupServerState { Url = "https://cloud.test", Token = "t" })!.Value.Server.ToString());
    }
}
