using System.Net;
using System.Security.Cryptography;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class TransportTests
{
    private sealed class Peer : IDisposable
    {
        public readonly PeerIdentity Identity = PeerIdentity.Generate();
        public readonly UdpMux Mux = new();
        public readonly PeerNetwork Network;
        public IPEndPoint EndPoint => new(IPAddress.Loopback, Mux.LocalPort);

        public Peer()
        {
            Network = new PeerNetwork(Identity, Mux, TestUtil.FastNetwork);
            Mux.Start();
        }

        public void Dispose() { Network.Dispose(); Mux.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task Simultaneous_connect_establishes_an_authenticated_session()
    {
        using var a = new Peer(); using var b = new Peer();

        var ca = a.Network.Connect(b.EndPoint, b.Identity.Id);
        var cb = b.Network.Connect(a.EndPoint, a.Identity.Id);
        await Task.WhenAll(ca.WhenEstablished, cb.WhenEstablished).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(b.Identity.Id, ca.RemoteId);
        Assert.Equal(a.Identity.Id, cb.RemoteId);
    }

    [Fact]
    public async Task One_sided_connect_is_accepted_by_the_listener()
    {
        using var a = new Peer(); using var b = new Peer();
        var ca = a.Network.Connect(b.EndPoint);
        await ca.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        await TestUtil.WaitFor(() => b.Network.Get(a.Identity.Id) != null);
    }

    [Fact]
    public async Task Wrong_expected_identity_is_rejected()
    {
        using var a = new Peer(); using var b = new Peer();
        var impostor = PeerIdentity.Generate().Id;
        var ca = a.Network.Connect(b.EndPoint, impostor);
        await Assert.ThrowsAsync<IOException>(() => ca.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Reliable_delivery_survives_heavy_packet_loss()
    {
        using var a = new Peer(); using var b = new Peer();
        var ca = a.Network.Connect(b.EndPoint);
        await ca.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        await TestUtil.WaitFor(() => b.Network.Get(a.Identity.Id) != null);

        // Seeded, so a slow machine sees the same pattern of losses as a fast one.
        static Func<ReadOnlyMemory<byte>, System.Net.IPEndPoint, bool> Lossy(int seed)
        {
            var random = new Random(seed);
            return (_, _) => { lock (random) return random.NextDouble() > 0.3; };
        }
        a.Mux.OutboundFilter = Lossy(1);
        b.Mux.OutboundFilter = Lossy(2);

        var received = new List<byte[]>();
        b.Network.RegisterChannel(7, (_, _, data, reliable) => { if (reliable) lock (received) received.Add(data.ToArray()); });

        var messages = Enumerable.Range(0, 6).Select(i => RandomNumberGenerator.GetBytes(i == 3 ? 40_000 : 500 + i)).ToList();
        foreach (var m in messages) ca.SendReliable(7, m);

        await TestUtil.WaitFor(() => { lock (received) return received.Count == messages.Count; }, 40_000);
        lock (received)
            for (int i = 0; i < messages.Count; i++) Assert.Equal(messages[i], received[i]);
    }

    [Fact]
    public async Task Unreliable_datagrams_are_delivered()
    {
        using var a = new Peer(); using var b = new Peer();
        var ca = a.Network.Connect(b.EndPoint);
        await ca.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        await TestUtil.WaitFor(() => b.Network.Get(a.Identity.Id) != null);

        byte[]? got = null;
        b.Network.RegisterChannel(16, (_, _, data, reliable) => { if (!reliable) got = data.ToArray(); });
        ca.SendUnreliable(16, new byte[] { 1, 2, 3 });
        await TestUtil.WaitFor(() => got != null);
        Assert.Equal(new byte[] { 1, 2, 3 }, got);
    }

    [Fact]
    public async Task Forged_and_replayed_packets_are_ignored()
    {
        using var a = new Peer(); using var b = new Peer();
        var ca = a.Network.Connect(b.EndPoint);
        await ca.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        await TestUtil.WaitFor(() => b.Network.Get(a.Identity.Id) != null);

        int count = 0;
        var captured = new List<byte[]>();
        b.Network.RegisterChannel(9, (_, _, _, _) => Interlocked.Increment(ref count));
        a.Mux.OutboundFilter = (packet, _) => { lock (captured) captured.Add(packet.ToArray()); return true; };

        ca.SendUnreliable(9, new byte[] { 1 });
        await TestUtil.WaitFor(() => count == 1);

        byte[] original; lock (captured) original = captured.Last(p => p[1] == 2);
        var tampered = (byte[])original.Clone(); tampered[^1] ^= 0xFF;
        using var attacker = new System.Net.Sockets.UdpClient();
        await attacker.SendAsync(original, b.EndPoint);
        await attacker.SendAsync(tampered, b.EndPoint);
        a.Mux.OutboundFilter = null;
        await Task.Delay(300);
        Assert.Equal(1, count);
    }
}

public class RestartTests
{
    [Fact]
    public async Task A_restarted_peer_replaces_its_dead_session_instead_of_being_rejected()
    {
        var options = new PeerNetworkOptions { HelloInterval = TimeSpan.FromMilliseconds(100) };
        using var a = PeerIdentity.Generate();
        using var bId = PeerIdentity.Generate();
        using var aMux = new UdpMux();
        using var aNet = new PeerNetwork(a, aMux, options);
        aMux.Start();
        var aEp = new IPEndPoint(IPAddress.Loopback, aMux.LocalPort);

        // The session id tie-break is random, so repeat to cover both outcomes.
        for (int i = 0; i < 12; i++)
        {
            var bMux1 = new UdpMux();
            var bNet1 = new PeerNetwork(bId, bMux1, options);
            bMux1.Start();
            await bNet1.Connect(aEp, a.Id).WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
            await TestUtil.WaitFor(() => aNet.Get(bId.Id) != null);
            await Task.Delay(500);

            // B dies without saying goodbye: its socket just goes away.
            bMux1.Dispose();

            using var bMux2 = new UdpMux();
            using var bNet2 = new PeerNetwork(bId, bMux2, options);
            bMux2.Start();
            await bNet2.Connect(aEp, a.Id).WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
            var expected = new IPEndPoint(IPAddress.Loopback, bMux2.LocalPort);
            await TestUtil.WaitFor(() => aNet.Get(bId.Id) is { State: ConnectionState.Established } c && c.RemoteEndPoint.Equals(expected));
            bNet1.Dispose();
        }
    }

    [Fact]
    public async Task Data_that_is_never_acknowledged_closes_the_connection()
    {
        var options = new PeerNetworkOptions { HelloInterval = TimeSpan.FromMilliseconds(100), UnackedTimeout = TimeSpan.FromSeconds(1) };
        using var aId = PeerIdentity.Generate();
        using var bId = PeerIdentity.Generate();
        using var aMux = new UdpMux();
        using var bMux = new UdpMux();
        using var aNet = new PeerNetwork(aId, aMux, options);
        using var bNet = new PeerNetwork(bId, bMux, options);
        aMux.Start(); bMux.Start();

        var conn = aNet.Connect(new IPEndPoint(IPAddress.Loopback, bMux.LocalPort), bId.Id);
        await conn.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));

        bMux.Dispose();
        conn.SendReliable(1, new byte[] { 1, 2, 3 });

        await TestUtil.WaitFor(() => conn.State == ConnectionState.Closed);
        Assert.Contains("acknowledge", conn.CloseReason);
    }
}
