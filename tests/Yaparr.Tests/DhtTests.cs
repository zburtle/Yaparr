using System.Net;
using Yaparr.Discovery.Dht;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class DhtTests
{
    [Fact]
    public async Task Announced_peers_can_be_found_through_a_dht_node()
    {
        using var serverMux = new UdpMux();
        using var server = new DhtNode(serverMux, new DhtOptions { ReadOnly = false, BootstrapHosts = Array.Empty<string>() });
        serverMux.Start();

        var clientOptions = new DhtOptions { BootstrapHosts = new[] { $"127.0.0.1:{serverMux.LocalPort}" } };
        using var aMux = new UdpMux(); using var a = new DhtNode(aMux, clientOptions); aMux.Start();
        using var bMux = new UdpMux(); using var b = new DhtNode(bMux, clientOptions); bMux.Start();

        var topic = System.Security.Cryptography.SHA1.HashData("test-topic"u8);
        Assert.Empty(await b.GetPeersAsync(topic));

        Assert.True(await a.AnnounceAsync(topic) >= 1);

        var found = await b.GetPeersAsync(topic);
        Assert.Contains(new IPEndPoint(IPAddress.Loopback, aMux.LocalPort), found);
    }

    [Fact]
    public async Task Bootstrap_survives_dns_failure_using_remembered_nodes_or_built_in_addresses()
    {
        using var serverMux = new UdpMux();
        using var server = new DhtNode(serverMux, new DhtOptions { ReadOnly = false, BootstrapHosts = Array.Empty<string>() });
        serverMux.Start();
        var serverAddress = $"127.0.0.1:{serverMux.LocalPort}";
        var topic = System.Security.Cryptography.SHA1.HashData("dns-down"u8);
        var unresolvable = new[] { "no-such-host.invalid:6881" };

        // No usable address anywhere: nothing is found, and it does not hang.
        using (var lostMux = new UdpMux())
        using (var lost = new DhtNode(lostMux, new DhtOptions { BootstrapHosts = unresolvable, FallbackSeeds = Array.Empty<string>() }))
        {
            lostMux.Start();
            Assert.Empty(await lost.GetPeersAsync(topic));
            Assert.Equal(0, lost.KnownNodes);
        }

        // Built-in addresses are used when every name fails.
        using (var aMux = new UdpMux())
        using (var a = new DhtNode(aMux, new DhtOptions { BootstrapHosts = unresolvable, FallbackSeeds = new[] { serverAddress } }))
        {
            aMux.Start();
            Assert.True(await a.AnnounceAsync(topic) >= 1);
            Assert.True(a.KnownNodes > 0);
        }

        // Nodes remembered from an earlier run are used too.
        var cache = Path.Combine(Path.GetTempPath(), "yaparr-dht-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(cache, serverAddress + Environment.NewLine);
            using var bMux = new UdpMux();
            using var b = new DhtNode(bMux, new DhtOptions { BootstrapHosts = unresolvable, FallbackSeeds = Array.Empty<string>(), NodeCachePath = cache });
            bMux.Start();
            Assert.Single(await b.GetPeersAsync(topic));
        }
        finally { File.Delete(cache); }
    }

    [Fact]
    public async Task Announce_with_a_bad_token_is_rejected()
    {
        using var serverMux = new UdpMux();
        using var server = new DhtNode(serverMux, new DhtOptions { ReadOnly = false, BootstrapHosts = Array.Empty<string>() });
        serverMux.Start();

        using var raw = new System.Net.Sockets.UdpClient();
        var msg = new BDict
        {
            ["t"] = "aa", ["y"] = "q", ["q"] = "announce_peer",
            ["a"] = new BDict { ["id"] = new byte[20], ["info_hash"] = new byte[20], ["port"] = 1234L, ["token"] = "bogus" },
        };
        var bytes = Bencode.Encode(msg);
        await raw.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, serverMux.LocalPort));
        var reply = (BDict)Bencode.Decode((await raw.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer);
        Assert.Equal("e", reply.Str("y"));
    }
}
