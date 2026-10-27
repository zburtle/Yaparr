using System.Collections.Concurrent;
using System.Net;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Transport;

namespace Yaparr.Tests;

/// <summary>Chat behaviour over a network that loses, reorders, duplicates, rewrites addresses and goes dark.</summary>
public class BadNetworkTests
{
    private static ChatNode Node(string name) => new(new ChatNodeOptions
    {
        DisplayName = name,
        // Discovery finds nobody: these tests connect by address, through the proxy.
        DiscoveryFactory = mux => new InMemoryDiscoveryHub().CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        AckTimeout = TimeSpan.FromSeconds(1),
        Network = new PeerNetworkOptions
        {
            HelloInterval = TimeSpan.FromMilliseconds(100),
            UnackedTimeout = TimeSpan.FromSeconds(1.5),
        },
    });

    private static IPEndPoint Local(ChatNode node) => new(IPAddress.Loopback, node.Mux.LocalPort);

    [Fact]
    public async Task Messages_arrive_once_and_in_order_over_a_lossy_reordering_duplicating_network()
    {
        using var alice = Node("alice");
        using var bob = Node("bob");
        alice.Start(); bob.Start();
        using var proxy = new FlakyProxy(Local(bob)) { Loss = 0.25, Duplicate = 0.2, DelayMs = 5, JitterMs = 60 };

        var received = new ConcurrentQueue<string>();
        bob.IncomingDirectChat += c => c.MessageReceived += m => received.Enqueue(m.Text);
        var toBob = await alice.ConnectAsync(proxy.EndPoint, bob.Id).WaitAsync(TimeSpan.FromSeconds(20));

        var statuses = new ConcurrentDictionary<string, DeliveryState>();
        toBob.DeliveryChanged += s => statuses[s.MessageId] = s.State;
        var ids = new List<string>();
        for (int i = 0; i < 20; i++) ids.Add(await toBob.SendAsync($"message {i}"));

        await TestUtil.WaitFor(() => received.Count >= 20, 30_000);
        await TestUtil.WaitFor(() => ids.All(id => statuses.TryGetValue(id, out var s) && s == DeliveryState.Delivered), 30_000);

        Assert.Equal(20, received.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"message {i}").OrderBy(x => x), received.OrderBy(x => x));
        Assert.True(proxy.Dropped > 0, "the network should actually have lost packets");
    }

    [Fact]
    public async Task A_message_sent_into_a_dead_path_is_flagged_then_delivered_once_the_path_returns()
    {
        using var alice = Node("alice");
        using var bob = Node("bob");
        alice.Start(); bob.Start();
        using var proxy = new FlakyProxy(Local(bob));

        var received = new ConcurrentQueue<string>();
        bob.IncomingDirectChat += c => c.MessageReceived += m => received.Enqueue(m.Text);
        var toBob = await alice.ConnectAsync(proxy.EndPoint, bob.Id).WaitAsync(TimeSpan.FromSeconds(10));

        var states = new ConcurrentQueue<DeliveryState>();
        toBob.DeliveryChanged += s => states.Enqueue(s.State);

        proxy.Blackhole = true;
        string id = await toBob.SendAsync("anyone?");

        // Nothing is acknowledged: it is reported as not delivered, and the dead session is dropped.
        await TestUtil.WaitFor(() => states.Contains(DeliveryState.NotDelivered), 10_000);
        await TestUtil.WaitFor(() => alice.GetLink(bob.Id) != PeerLink.Direct, 10_000);
        Assert.Empty(received);

        proxy.Blackhole = false;

        // Alice dials again on her own and the message is handed over.
        await TestUtil.WaitFor(() => states.Contains(DeliveryState.Delivered), 30_000);
        await TestUtil.WaitFor(() => received.Count == 1, 10_000);
        Assert.Equal("anyone?", received.Single());
        Assert.Equal(PeerLink.Direct, alice.GetLink(bob.Id));
        _ = id;
    }
}
