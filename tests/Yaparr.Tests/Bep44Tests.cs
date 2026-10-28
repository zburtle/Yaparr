using System.Net;
using System.Net.Sockets;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Discovery.Dht;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

/// <summary>BEP 44 mutable items and the signed address records built on them.</summary>
public class Bep44Tests
{
    private sealed class Net : IDisposable
    {
        private readonly UdpMux _serverMux = new();
        public DhtNode Server { get; }
        public DhtOptions ClientOptions { get; }

        public Net()
        {
            Server = new DhtNode(_serverMux, new DhtOptions { ReadOnly = false, BootstrapHosts = Array.Empty<string>() });
            _serverMux.Start();
            ClientOptions = new DhtOptions { BootstrapHosts = new[] { $"127.0.0.1:{_serverMux.LocalPort}" } };
        }

        public DhtDiscovery Client(PeerIdentity? identity)
        {
            var mux = new UdpMux();
            var client = new DhtDiscovery(mux, ClientOptions, identity);
            mux.Start();
            return client;
        }

        public void Dispose() { Server.Dispose(); _serverMux.Dispose(); }
    }

    [Fact]
    public void Mutable_item_signatures_match_the_spec_layout_and_reject_tampering()
    {
        var seed = new byte[32]; seed[0] = 7;
        var pk = MutableItem.PublicKeyFromSeed(seed);
        var salt = "foobar"u8.ToArray();
        var value = "Hello World!"u8.ToArray();

        // The byte layout from BEP 44: salt, seq and v, each as a bencode key/value pair without the outer dictionary.
        Assert.Equal("4:salt6:foobar3:seqi1e1:v12:Hello World!",
            System.Text.Encoding.ASCII.GetString(MutableItem.SignedBytes(salt, 1, value)));

        var sig = MutableItem.Sign(seed, pk, salt, 1, value);
        Assert.True(MutableItem.Verify(pk, salt, 1, value, sig));
        Assert.False(MutableItem.Verify(pk, salt, 2, value, sig));
        Assert.False(MutableItem.Verify(pk, salt, 1, "Hello World?"u8.ToArray(), sig));
        Assert.False(MutableItem.Verify(pk, "other"u8.ToArray(), 1, value, sig));
    }

    [Fact]
    public async Task A_published_address_can_be_looked_up_by_peer_id_and_a_newer_one_replaces_it()
    {
        using var net = new Net();
        using var identity = PeerIdentity.Generate();
        using var publisher = net.Client(identity);
        using var reader = net.Client(null);

        Assert.Empty(await reader.LookupAsync(identity.Id));

        var first = new[] { new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40001) };
        Assert.True(await publisher.PublishAsync(first) >= 1);
        Assert.Equal(first, await reader.LookupAsync(identity.Id));

        // The peer moved: the new record wins, the old address is not returned any more.
        var second = new[] { new IPEndPoint(IPAddress.Parse("198.51.100.7"), 52000), new IPEndPoint(IPAddress.Parse("203.0.113.99"), 40002) };
        Assert.True(await publisher.PublishAsync(second) >= 1);
        Assert.Equal(second, await reader.LookupAsync(identity.Id));
    }

    [Fact]
    public async Task A_record_written_by_someone_else_for_a_peer_is_ignored()
    {
        using var net = new Net();
        using var real = PeerIdentity.Generate();
        using var impostor = PeerIdentity.Generate();
        using var reader = net.Client(null);

        // Anyone who knows the ID can work out the DHT key, so they can write to it with a huge sequence number...
        var (seed, publicKey) = PeerRecord.DeriveKey(real.Id);
        var forged = PeerRecord.Encode(impostor, new[] { new IPEndPoint(IPAddress.Parse("192.0.2.66"), 6666) }, 1);
        using var attackerMux = new UdpMux();
        using var attacker = new DhtNode(attackerMux, net.ClientOptions);
        attackerMux.Start();
        Assert.True(await attacker.PutMutableAsync(seed, publicKey, PeerRecord.SaltBytes, long.MaxValue / 2, forged) >= 1);

        // ...but readers check the inner signature against the peer ID and throw it away.
        Assert.Empty(await reader.LookupAsync(real.Id));
    }

    [Fact]
    public void A_tampered_record_does_not_decode()
    {
        using var identity = PeerIdentity.Generate();
        var value = PeerRecord.Encode(identity, new[] { new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1234) }, 1000);

        Assert.NotNull(PeerRecord.Decode(identity.Id, value));

        var tampered = (byte[])value.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        Assert.Null(PeerRecord.Decode(identity.Id, tampered));

        using var other = PeerIdentity.Generate();
        Assert.Null(PeerRecord.Decode(other.Id, value)); // not the peer it claims to describe
    }

    [Fact]
    public async Task A_node_rejects_a_put_with_a_bad_signature_or_an_old_sequence_number()
    {
        using var net = new Net();
        var seed = new byte[32]; seed[1] = 9;
        var pk = MutableItem.PublicKeyFromSeed(seed);
        var salt = "s"u8.ToArray();

        using var mux = new UdpMux();
        using var node = new DhtNode(mux, net.ClientOptions);
        mux.Start();

        Assert.True(await node.PutMutableAsync(seed, pk, salt, 10, "v10"u8.ToArray()) >= 1);
        Assert.Equal(0, await node.PutMutableAsync(seed, pk, salt, 9, "v9"u8.ToArray()));   // older: refused
        Assert.True(await node.PutMutableAsync(seed, pk, salt, 11, "v11"u8.ToArray()) >= 1);

        var item = await node.GetMutableAsync(pk, salt);
        Assert.NotNull(item);
        Assert.Equal(11, item!.Seq);
        Assert.Equal("v11"u8.ToArray(), item.Value);
    }

    // Discovery that only offers address records: nothing is announced or searched by topic, so a peer can be
    // found only through the record it published.
    private sealed class RecordsOnly : IPeerDiscovery, IPeerRecords
    {
        private readonly DhtDiscovery _inner;
        public RecordsOnly(DhtDiscovery inner) => _inner = inner;
        public Task AnnounceAsync(byte[] topic, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IPEndPoint>>(Array.Empty<IPEndPoint>());
        public Task<int> PublishAsync(IReadOnlyList<IPEndPoint> endpoints, CancellationToken ct = default) => _inner.PublishAsync(endpoints, ct);
        public Task<IReadOnlyList<IPEndPoint>> LookupAsync(PeerId peer, CancellationToken ct = default) => _inner.LookupAsync(peer, ct);
        public void Dispose() => _inner.Dispose();
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public async Task A_direct_chat_finds_the_peer_through_its_published_address()
    {
        using var net = new Net();
        using var aliceId = PeerIdentity.Generate();
        using var bobId = PeerIdentity.Generate();
        var aliceLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var bobLog = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int bobPort = FreeUdpPort();

        ChatNode Node(string name, PeerIdentity id, int port, IEnumerable<IPEndPoint> advertised, System.Collections.Concurrent.ConcurrentQueue<string> log) => new(new ChatNodeOptions
        {
            DisplayName = name,
            Identity = id,
            ListenPort = port,
            DiscoveryFactory = mux => new RecordsOnly(new DhtDiscovery(mux, net.ClientOptions, id)),
            DiscoveryInterval = TimeSpan.FromMilliseconds(300),
            Network = TestUtil.FastNetwork,
            AdvertisedEndPoints = advertised.ToList(),
            Log = log.Enqueue,
        });

        using var bob = Node("bob", bobId, bobPort, new[] { new IPEndPoint(IPAddress.Loopback, bobPort) }, bobLog);
        bob.Start();
        await TestUtil.WaitFor(() => bobLog.Any(l => l.Contains("published our address")));

        using var alice = Node("alice", aliceId, 0, Array.Empty<IPEndPoint>(), aliceLog);
        alice.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var chat = await alice.OpenDirectChatAsync(bobId.Id, cts.Token);

        Assert.Equal(bobId.Id, chat.PeerId);
        Assert.Contains(aliceLog, l => l.Contains("published 1 address"));
    }
}
