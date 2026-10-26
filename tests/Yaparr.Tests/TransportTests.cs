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

        a.Mux.OutboundFilter = (_, _) => Random.Shared.NextDouble() > 0.35;
        b.Mux.OutboundFilter = (_, _) => Random.Shared.NextDouble() > 0.35;

        var received = new List<byte[]>();
        b.Network.RegisterChannel(7, (_, _, data, reliable) => { if (reliable) lock (received) received.Add(data.ToArray()); });

        var messages = Enumerable.Range(0, 6).Select(i => RandomNumberGenerator.GetBytes(i == 3 ? 40_000 : 500 + i)).ToList();
        foreach (var m in messages) ca.SendReliable(7, m);

        await TestUtil.WaitFor(() => { lock (received) return received.Count == messages.Count; }, 20_000);
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
