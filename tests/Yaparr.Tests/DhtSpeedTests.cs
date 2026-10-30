using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Yaparr.Chat;
using Yaparr.Discovery.Dht;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class DhtSpeedTests
{
    /// <summary>A node that answers every query with a list of other nodes: silent ones that never answer, and one real one.</summary>
    private sealed class Directory : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly List<UdpClient> _silent = new();
        private readonly byte[] _nodes;
        private readonly CancellationTokenSource _stop = new();

        public Directory(int silentCount, byte[] liveId, int livePort)
        {
            var list = new List<byte>();
            for (int i = 0; i < silentCount; i++)
            {
                var s = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                _silent.Add(s);
                list.AddRange(Compact(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20), ((IPEndPoint)s.Client.LocalEndPoint!).Port));
            }
            list.AddRange(Compact(liveId, livePort));
            _nodes = list.ToArray();
            _ = Task.Run(Loop);
        }

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        private static byte[] Compact(byte[] id, int port) =>
            id.Concat(IPAddress.Loopback.GetAddressBytes()).Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray();

        private async Task Loop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var r = await _udp.ReceiveAsync(_stop.Token);
                    var query = (BDict)Bencode.Decode(r.Buffer);
                    var reply = new BDict
                    {
                        ["t"] = query.Bytes("t")!, ["y"] = "r",
                        ["r"] = new BDict { ["id"] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20), ["nodes"] = _nodes },
                    };
                    await _udp.SendAsync(Bencode.Encode(reply), r.RemoteEndPoint, _stop.Token);
                }
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _udp.Dispose();
            foreach (var s in _silent) s.Dispose();
        }
    }

    /// <summary>An id next to the topic, so the lookup asks that node first, as it would a node that is really close.</summary>
    private static byte[] NearTo(byte[] topic) { var id = (byte[])topic.Clone(); id[^1] ^= 1; return id; }

    [Fact]
    public async Task Lookup_is_not_held_up_by_nodes_that_never_answer()
    {
        using var liveMux = new UdpMux();
        using var live = new DhtNode(liveMux, new DhtOptions { ReadOnly = false, BootstrapHosts = Array.Empty<string>() });
        liveMux.Start();
        var topic = System.Security.Cryptography.SHA1.HashData("silent-nodes"u8);

        using (var aMux = new UdpMux())
        using (var a = new DhtNode(aMux, new DhtOptions { BootstrapHosts = new[] { $"127.0.0.1:{liveMux.LocalPort}" } }))
        {
            aMux.Start();
            Assert.True(await a.AnnounceAsync(topic) >= 1);
        }

        using var directory = new Directory(30, NearTo(topic), liveMux.LocalPort);
        using var bMux = new UdpMux();
        using var b = new DhtNode(bMux, new DhtOptions
        {
            BootstrapHosts = new[] { $"127.0.0.1:{directory.Port}" },
            FallbackSeeds = Array.Empty<string>(),
            QueryTimeout = TimeSpan.FromSeconds(1.5),
        });
        bMux.Start();

        var watch = Stopwatch.StartNew();
        var found = await b.GetPeersAsync(topic);
        watch.Stop();

        Assert.Single(found);
        // Neither the bootstrap nor the search may wait for all 30 silent nodes to time out (five waits of 1.5 s even six at a time).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(9), $"took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task A_second_search_does_not_wait_for_nodes_that_stayed_silent_the_first_time()
    {
        using var liveMux = new UdpMux();
        using var live = new DhtNode(liveMux, new DhtOptions { ReadOnly = false, BootstrapHosts = Array.Empty<string>() });
        liveMux.Start();
        var topic = System.Security.Cryptography.SHA1.HashData("silent-twice"u8);

        using (var aMux = new UdpMux())
        using (var a = new DhtNode(aMux, new DhtOptions { BootstrapHosts = new[] { $"127.0.0.1:{liveMux.LocalPort}" } }))
        {
            aMux.Start();
            Assert.True(await a.AnnounceAsync(topic) >= 1);
        }

        // The node that knows the peer is the farthest of all from the topic, behind 30 that never answer.
        var far = (byte[])topic.Clone();
        far[0] ^= 0xFF;
        using var directory = new Directory(30, far, liveMux.LocalPort);
        using var bMux = new UdpMux();
        using var b = new DhtNode(bMux, new DhtOptions
        {
            BootstrapHosts = new[] { $"127.0.0.1:{directory.Port}" },
            FallbackSeeds = Array.Empty<string>(),
            QueryTimeout = TimeSpan.FromSeconds(1),
        });
        bMux.Start();

        var first = Stopwatch.StartNew();
        var firstPeers = await b.GetPeersAsync(topic);
        Assert.True(firstPeers.Count == 1, $"the first search found {firstPeers.Count} peers after {first.Elapsed.TotalSeconds:0.0} s, {b.SilentNodes} silent nodes remembered");
        // The search stops when the peer is found, so how many of the 30 had timed out by then depends on the speed of the machine.
        Assert.True(b.SilentNodes >= 10, $"only {b.SilentNodes} nodes were remembered as silent");

        var watch = Stopwatch.StartNew();
        Assert.Single(await b.GetPeersAsync(topic));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"the second search took {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public void Searches_come_quickly_while_alone_and_at_the_usual_pace_otherwise()
    {
        var interval = TimeSpan.FromSeconds(30);
        Assert.Equal(TimeSpan.FromSeconds(4), ChatNode.NextDiscoveryDelay(interval, alone: true, quickSoFar: 0));
        Assert.Equal(TimeSpan.FromSeconds(4), ChatNode.NextDiscoveryDelay(interval, alone: true, quickSoFar: 7));
        Assert.Equal(interval, ChatNode.NextDiscoveryDelay(interval, alone: true, quickSoFar: 8));
        Assert.Equal(interval, ChatNode.NextDiscoveryDelay(interval, alone: false, quickSoFar: 0));
        // A short interval (tests) is never stretched.
        Assert.Equal(TimeSpan.FromSeconds(1), ChatNode.NextDiscoveryDelay(TimeSpan.FromSeconds(1), alone: true, quickSoFar: 0));
    }
}
