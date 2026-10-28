using System.Net;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class DuplicateConnectionTests
{
    [Fact]
    public async Task Two_peers_dialing_each_other_at_once_settle_on_one_connection()
    {
        var options = TestUtil.FastNetwork;
        for (int round = 0; round < 10; round++)
        {
            using var aId = PeerIdentity.Generate();
            using var bId = PeerIdentity.Generate();
            using var aMux = new UdpMux();
            using var bMux = new UdpMux();
            using var a = new PeerNetwork(aId, aMux, options);
            using var b = new PeerNetwork(bId, bMux, options);
            aMux.Start(); bMux.Start();
            int established = 0, closed = 0;
            a.ConnectionEstablished += _ => Interlocked.Increment(ref established);
            a.ConnectionClosed += _ => Interlocked.Increment(ref closed);
            b.ConnectionEstablished += _ => { };

            var aEp = new IPEndPoint(IPAddress.Loopback, aMux.LocalPort);
            var bEp = new IPEndPoint(IPAddress.Loopback, bMux.LocalPort);
            var t1 = Task.Run(() => a.Connect(bEp, bId.Id));
            var t2 = Task.Run(() => b.Connect(aEp, aId.Id));
            await Task.WhenAll(t1, t2);

            await Task.Delay(1500);
            Assert.True(established <= 2 && closed <= 1, $"round {round}: {established} established, {closed} closed on A");
            Assert.NotNull(a.Get(bId.Id));
            Assert.NotNull(b.Get(aId.Id));
        }
    }
}

public class FlappingTests
{
    // The same identity running twice (two app instances, or an old process still alive) used to make the peer
    // swap the two connections back and forth many times a second.
    [Fact]
    public async Task Two_instances_of_one_identity_do_not_make_the_peer_swap_connections_endlessly()
    {
        var options = TestUtil.FastNetwork;
        using var aId = PeerIdentity.Generate();
        using var bId = PeerIdentity.Generate();
        using var aMux = new UdpMux();
        using var a = new PeerNetwork(aId, aMux, options);
        aMux.Start();
        var aEp = new IPEndPoint(IPAddress.Loopback, aMux.LocalPort);
        int established = 0;
        a.ConnectionEstablished += _ => Interlocked.Increment(ref established);

        using var cts = new CancellationTokenSource();
        var muxes = new List<UdpMux>(); var nets = new List<PeerNetwork>();
        for (int i = 0; i < 2; i++)
        {
            var mux = new UdpMux(); var net = new PeerNetwork(bId, mux, options);
            mux.Start(); muxes.Add(mux); nets.Add(net);
            // Like the redial loop: whenever the connection is gone, dial again.
            _ = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try { net.Connect(aEp, aId.Id); } catch { }
                    try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        await Task.Delay(4000);
        cts.Cancel();
        int swaps = established;
        foreach (var n in nets) n.Dispose();
        foreach (var m in muxes) m.Dispose();
        Assert.True(swaps < 15, $"the peer swapped connections {swaps} times in 4 seconds");
    }
}
