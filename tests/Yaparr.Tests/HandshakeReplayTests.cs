using System.Net;
using System.Net.Sockets;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

/// <summary>
/// A hello is a signed statement that a temporary key is the author's, and anyone who ever connected to the author has one. These check
/// that repeating it proves nothing: it neither makes a connection nor breaks one.
/// </summary>
public class HandshakeReplayTests
{
    private sealed class Peer : IDisposable
    {
        public readonly PeerIdentity Identity = PeerIdentity.Generate();
        public readonly UdpMux Mux = new();
        public readonly PeerNetwork Network;
        public readonly List<byte[]> SentHellos = new();
        public IPEndPoint EndPoint => new(IPAddress.Loopback, Mux.LocalPort);

        public Peer()
        {
            Network = new PeerNetwork(Identity, Mux, TestUtil.FastNetwork);
            // Keeps a copy of every hello this peer sends (an eavesdropper, or anyone it ever connected to, has them too).
            Mux.OutboundFilter = (data, _) =>
            {
                if (data.Length > 1 && data.Span[1] == 1) lock (SentHellos) SentHellos.Add(data.ToArray());
                return true;
            };
            Mux.Start();
        }

        public byte[] FirstHello() { lock (SentHellos) return SentHellos[0]; }

        public void Dispose() { Network.Dispose(); Mux.Dispose(); Identity.Dispose(); }
    }

    [Fact]
    public async Task A_repeated_hello_from_a_stranger_does_not_make_a_connection()
    {
        using var a = new Peer(); using var b = new Peer(); using var c = new Peer();
        await a.Network.Connect(b.EndPoint, b.Identity.Id).WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        var hello = a.FirstHello();

        // Someone who is not a sends a's hello to c, from an address of their own.
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        for (int i = 0; i < 20; i++) await stranger.SendAsync(hello, hello.Length, c.EndPoint);
        await Task.Delay(1500);

        Assert.Null(c.Network.Get(a.Identity.Id));
    }

    [Fact]
    public async Task Hellos_from_many_addresses_fill_only_a_small_waiting_room_that_empties_again()
    {
        using var a = new Peer(); using var b = new Peer(); using var c = new Peer();
        await a.Network.Connect(b.EndPoint, b.Identity.Id).WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        var hello = a.FirstHello();

        var strangers = Enumerable.Range(0, 40).Select(_ => new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))).ToList();
        try
        {
            foreach (var s in strangers) await s.SendAsync(hello, hello.Length, c.EndPoint);
            await Task.Delay(500);

            // Four from one address, and none of them is a connection.
            Assert.InRange(c.Network.ProvisionalCount, 1, 4);
            Assert.Null(c.Network.Get(a.Identity.Id));

            // They go away by themselves once nothing proves anything.
            await TestUtil.WaitFor(() => c.Network.ProvisionalCount == 0, 20_000);
            Assert.Null(c.Network.Get(a.Identity.Id));
        }
        finally { foreach (var s in strangers) s.Dispose(); }
    }

    [Fact]
    public async Task An_old_hello_repeated_at_a_live_connection_does_not_break_it()
    {
        using var a = new Peer(); using var b = new Peer();
        var first = a.Network.Connect(b.EndPoint, b.Identity.Id);
        await first.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        await TestUtil.WaitFor(() => b.Network.Get(a.Identity.Id) != null);
        var old = a.FirstHello();                  // from the first connection, with its temporary key
        first.Close();
        await TestUtil.WaitFor(() => b.Network.Get(a.Identity.Id) == null);

        var second = a.Network.Connect(b.EndPoint, b.Identity.Id);   // a new connection, a new temporary key
        await second.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        await TestUtil.WaitFor(() => b.Network.Get(a.Identity.Id) != null);

        var atB = new List<byte[]>(); var atA = new List<byte[]>();
        b.Network.RegisterChannel(7, (_, _, data, reliable) => { lock (atB) atB.Add(data.ToArray()); });
        a.Network.RegisterChannel(7, (_, _, data, reliable) => { lock (atA) atA.Add(data.ToArray()); });

        // The old hello arrives again, from a's own address (anyone who can send as a, or just a late copy).
        for (int i = 0; i < 5; i++) a.Mux.Send(old, b.EndPoint);
        await Task.Delay(500);

        // The connection still carries data both ways.
        second.SendReliable(7, new byte[] { 1, 2, 3 });
        b.Network.Get(a.Identity.Id)!.SendReliable(7, new byte[] { 4, 5, 6 });
        await TestUtil.WaitFor(() => { lock (atB) lock (atA) return atB.Count == 1 && atA.Count == 1; });
        Assert.Equal(new byte[] { 1, 2, 3 }, atB[0]);
        Assert.Equal(new byte[] { 4, 5, 6 }, atA[0]);
        Assert.Equal(ConnectionState.Established, second.State);
    }

    [Fact]
    public async Task A_peer_that_restarts_still_replaces_its_old_connection()
    {
        // The case the candidate sessions must not break: the same identity comes back with a new temporary key.
        using var b = new Peer();
        var identity = PeerIdentity.Generate();
        int port;
        {
            using var mux1 = new UdpMux(); using var net1 = new PeerNetwork(identity, mux1, TestUtil.FastNetwork);
            mux1.Start(); port = mux1.LocalPort;
            await net1.Connect(b.EndPoint, b.Identity.Id).WhenEstablished.WaitAsync(TimeSpan.FromSeconds(5));
            await TestUtil.WaitFor(() => b.Network.Get(identity.Id) != null);
        }

        // Gone without saying goodbye; the new run uses the same identity.
        using var mux2 = new UdpMux(); using var net2 = new PeerNetwork(identity, mux2, TestUtil.FastNetwork);
        mux2.Start();
        var received = new List<byte[]>();
        b.Network.RegisterChannel(9, (_, _, data, _) => { lock (received) received.Add(data.ToArray()); });
        var again = net2.Connect(b.EndPoint, b.Identity.Id);
        await again.WhenEstablished.WaitAsync(TimeSpan.FromSeconds(10));
        again.SendReliable(9, new byte[] { 7 });
        await TestUtil.WaitFor(() => { lock (received) return received.Count == 1; });
        identity.Dispose();
        Assert.NotEqual(0, port);
    }
}
