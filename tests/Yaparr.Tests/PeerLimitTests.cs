using System.Collections.Concurrent;
using System.Net;
using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

// Any peer can send us as much as it likes; what is over its allowance is dropped, and a peer that keeps on is disconnected.
public class PeerLimitTests
{
    [Fact]
    public void A_bucket_allows_a_burst_then_the_refill_rate()
    {
        var bucket = new TokenBucket(capacity: 5, perSecond: 2);
        for (int i = 0; i < 5; i++) Assert.True(bucket.TryTake(now: 1000));
        Assert.False(bucket.TryTake(now: 1000));
        Assert.False(bucket.TryTake(now: 1400));   // 0.8 of a token
        Assert.True(bucket.TryTake(now: 1600));    // more than one whole token after 0.6 s
        Assert.False(bucket.TryTake(now: 1600));
    }

    [Fact]
    public void A_bucket_never_holds_more_than_its_capacity()
    {
        var bucket = new TokenBucket(capacity: 3, perSecond: 100);
        Assert.True(bucket.TryTake(now: 0));
        for (int i = 0; i < 3; i++) Assert.True(bucket.TryTake(now: 60_000));
        Assert.False(bucket.TryTake(now: 60_000));
    }

    [Fact]
    public void A_request_bigger_than_the_bucket_takes_all_of_it_instead_of_never_being_allowed()
    {
        var bucket = new TokenBucket(capacity: 10, perSecond: 1);
        Assert.True(bucket.TryTake(cost: 1000, now: 0));
        Assert.False(bucket.TryTake(now: 0));
    }

    [Fact]
    public void Drops_close_to_the_limit_in_a_short_time_ask_for_a_disconnect_but_slow_ones_do_not()
    {
        var rate = new PeerRate(new PeerLimits { DropsBeforeClose = 3, DropWindow = TimeSpan.FromMilliseconds(50) });
        Assert.False(rate.RecordDrop(out _));
        Assert.False(rate.RecordDrop(out _));
        Assert.True(rate.RecordDrop(out int total));
        Assert.Equal(3, total);

        Thread.Sleep(120);
        Assert.False(rate.RecordDrop(out total));
        Assert.Equal(1, total);
    }

    private static ChatNode Node(string name, PeerLimits? limits = null) => new(new ChatNodeOptions
    {
        DisplayName = name,
        Limits = limits ?? new PeerLimits(),
        DiscoveryFactory = mux => new InMemoryDiscoveryHub().CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
    });

    private static IPEndPoint Local(ChatNode node) => new(IPAddress.Loopback, node.Mux.LocalPort);

    [Fact]
    public async Task Messages_past_the_allowance_are_dropped_and_the_rest_arrive()
    {
        using var alice = Node("alice");
        using var bob = Node("bob", new PeerLimits { MessagesBurst = 5, MessagesPerSecond = 0.001 });
        alice.Start(); bob.Start();

        var received = new ConcurrentQueue<string>();
        bob.IncomingDirectChat += c => c.MessageReceived += m => received.Enqueue(m.Text);
        var toBob = await alice.ConnectAsync(Local(bob), bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 30; i++) await toBob.SendAsync($"message {i}");

        await TestUtil.WaitFor(() => received.Count >= 5);
        await Task.Delay(1500);
        Assert.Equal(5, received.Count);
    }

    [Fact]
    public async Task A_peer_that_keeps_sending_past_the_limit_is_disconnected()
    {
        using var alice = Node("alice");
        using var bob = Node("bob", new PeerLimits { MessagesBurst = 1, MessagesPerSecond = 0.001, DropsBeforeClose = 5 });
        alice.Start(); bob.Start();

        int disconnects = 0;
        bob.PeerDisconnected += _ => Interlocked.Increment(ref disconnects);
        var toBob = await alice.ConnectAsync(Local(bob), bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        try { for (int i = 0; i < 30; i++) await toBob.SendAsync($"message {i}"); }
        catch (InvalidOperationException) { }   // already cut off

        await TestUtil.WaitFor(() => Volatile.Read(ref disconnects) > 0);
    }

    [Fact]
    public async Task Ordinary_chatting_is_nowhere_near_the_default_limits()
    {
        using var alice = Node("alice");
        using var bob = Node("bob");
        alice.Start(); bob.Start();

        int count = 0;
        bob.IncomingDirectChat += c => c.MessageReceived += _ => Interlocked.Increment(ref count);
        var toBob = await alice.ConnectAsync(Local(bob), bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 30; i++) await toBob.SendAsync($"message {i}");

        await TestUtil.WaitFor(() => Volatile.Read(ref count) == 30);
    }
}
