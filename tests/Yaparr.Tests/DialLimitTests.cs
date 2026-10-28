using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

/// <summary>Discovery results come from anyone, so a flood of addresses must not turn into a flood of packets.</summary>
public class DialLimitTests
{
    private sealed class FixedDiscovery : IPeerDiscovery
    {
        private readonly IReadOnlyList<IPEndPoint> _found;
        public FixedDiscovery(IReadOnlyList<IPEndPoint> found) => _found = found;
        public Task AnnounceAsync(byte[] topic, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default) => Task.FromResult(_found);
        public void Dispose() { }
    }

    private sealed class Listeners : IDisposable
    {
        private readonly List<UdpClient> _clients = new();
        private readonly ConcurrentDictionary<int, int> _hits = new();
        private readonly CancellationTokenSource _cts = new();

        public Listeners(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                _clients.Add(client);
                int port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                _ = Task.Run(async () =>
                {
                    try { while (true) { await client.ReceiveAsync(_cts.Token); _hits.AddOrUpdate(port, 1, (_, n) => n + 1); } }
                    catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
                });
            }
        }

        public IReadOnlyList<IPEndPoint> EndPoints => _clients.Select(c => (IPEndPoint)c.Client.LocalEndPoint!).ToList();
        public int Contacted => _hits.Count;

        public void Dispose()
        {
            _cts.Cancel();
            foreach (var c in _clients) c.Dispose();
        }
    }

    [Fact]
    public async Task A_room_search_that_returns_many_addresses_only_contacts_a_few()
    {
        using var silent = new Listeners(100);
        using var node = new ChatNode(new ChatNodeOptions
        {
            DisplayName = "dialer",
            DiscoveryFactory = _ => new FixedDiscovery(silent.EndPoints),
            DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        });
        node.Start();
        await node.JoinPrivateRoomAsync(RoomKey.Generate(), "flood");

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.InRange(silent.Contacted, 1, 24);
    }

    [Fact]
    public async Task A_direct_chat_search_only_contacts_a_limited_number_of_addresses()
    {
        using var silent = new Listeners(100);
        using var node = new ChatNode(new ChatNodeOptions
        {
            DisplayName = "dialer",
            DiscoveryFactory = _ => new FixedDiscovery(silent.EndPoints),
        });
        node.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try { await node.OpenDirectChatAsync(new Yaparr.Identity.PeerId(new string('a', 64)), cts.Token); }
        catch (OperationCanceledException) { }

        Assert.InRange(silent.Contacted, 1, 16);
    }

    [Fact]
    public async Task An_address_nobody_answers_is_logged_and_backed_off()
    {
        using var silent = new Listeners(3);
        var log = new ConcurrentQueue<string>();
        using var node = new ChatNode(new ChatNodeOptions
        {
            DisplayName = "dialer",
            DiscoveryFactory = _ => new FixedDiscovery(silent.EndPoints),
            DiscoveryInterval = TimeSpan.FromMilliseconds(300),
            Network = new Yaparr.Transport.PeerNetworkOptions { ConnectTimeout = TimeSpan.FromSeconds(1) },
            Log = log.Enqueue,
        });
        node.Start();
        await node.JoinPrivateRoomAsync(RoomKey.Generate(), "quiet");

        await TestUtil.WaitFor(() => log.Any(l => l.Contains("could not connect to")), timeoutMs: 10_000);
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Each address was tried, failed, and is being left alone for longer each time.
        foreach (var ep in silent.EndPoints)
            Assert.InRange(log.Count(l => l.StartsWith($"dialing {ep}")), 1, 5);
        Assert.Contains(log, l => l.Contains("trying again in"));
    }

    [Fact]
    public void Our_own_old_addresses_are_recognised_as_ours()
    {
        var path = Path.Combine(Path.GetTempPath(), "yaparr-self-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var own = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40001);
            var cache = new PeerCache(path);
            Assert.False(cache.IsSelf(own));
            cache.RememberSelf(own);
            Assert.True(cache.IsSelf(own));
            Assert.False(cache.IsSelf(new IPEndPoint(own.Address, 40002)));

            cache.Save();
            Assert.True(new PeerCache(path).IsSelf(own)); // survives a restart
        }
        finally { File.Delete(path); }
    }
}
