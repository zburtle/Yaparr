using System.Collections.Concurrent;
using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

public class DeliveryTests
{
    [Fact]
    public async Task Room_message_is_reported_delivered_once_every_member_confirms()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        using var c = TestUtil.NewNode(hub, "c");
        var ra = await a.JoinRoomAsync("Lobby");
        await b.JoinRoomAsync("Lobby");
        await c.JoinRoomAsync("Lobby");
        // Members also lists people known only through others; a message is confirmed per direct connection.
        await TestUtil.WaitFor(() => ra.MemberStates.Count == 2);

        var seen = new ConcurrentQueue<DeliveryStatus>();
        ra.DeliveryChanged += seen.Enqueue;
        string id = await ra.SendAsync("hello all");

        await TestUtil.WaitFor(() => seen.Any(s => s.MessageId == id && s.State == DeliveryState.Delivered));
        var done = seen.Last(s => s.MessageId == id);
        Assert.Equal(2, done.Total);
        Assert.Equal(2, done.Delivered);
    }

    [Fact]
    public async Task Message_sent_to_an_empty_room_is_queued_and_delivered_when_someone_arrives()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        var ra = await a.JoinRoomAsync("Quiet");

        var seen = new ConcurrentQueue<DeliveryStatus>();
        ra.DeliveryChanged += seen.Enqueue;
        string id = await ra.SendAsync("anyone there?");
        Assert.Contains(seen, s => s.MessageId == id && s.State == DeliveryState.Queued);

        var got = new TaskCompletionSource<ChatMessage>();
        var rb = await b.JoinRoomAsync("Quiet");
        rb.MessageReceived += m => got.TrySetResult(m);
        // Late joiners also get history, so the message may already have arrived before we subscribed.
        await TestUtil.WaitFor(() => seen.Any(s => s.MessageId == id && s.State == DeliveryState.Delivered));
        Assert.Contains("anyone there?", rb.GetHistory().Select(m => m.Text));
    }

    [Fact]
    public async Task Direct_message_is_confirmed_and_not_shown_twice_when_resent()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        alice.Start(); bob.Start();
        await Task.Delay(100);

        int bobCount = 0;
        bob.IncomingDirectChat += c => c.MessageReceived += _ => Interlocked.Increment(ref bobCount);
        var toBob = await alice.OpenDirectChatAsync(bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var seen = new ConcurrentQueue<DeliveryStatus>();
        toBob.DeliveryChanged += seen.Enqueue;

        string id = await toBob.SendAsync("hi bob");
        await TestUtil.WaitFor(() => seen.Any(s => s.MessageId == id && s.State == DeliveryState.Delivered));

        toBob.Retry(id);
        await Task.Delay(400);
        Assert.Equal(1, bobCount);
    }
}

public class PeerCacheTests
{
    private static ChatNode Node(InMemoryDiscoveryHub hub, string name, string? cache) => new(new ChatNodeOptions
    {
        DisplayName = name,
        DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
        PeerCachePath = cache,
    });

    [Fact]
    public async Task A_restarted_node_dials_remembered_members_without_discovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-cache-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(dir, "peers.json");
        try
        {
            var hub = new InMemoryDiscoveryHub();
            using var b = Node(hub, "b", null);
            await b.JoinRoomAsync("Home");
            var a = Node(hub, "a", cache);
            var ra = await a.JoinRoomAsync("Home");
            await TestUtil.WaitFor(() => ra.Members.Count == 1);
            a.Dispose();
            Assert.True(File.Exists(cache));

            // Nothing can be found through discovery now: this hub is empty.
            using var a2 = Node(new InMemoryDiscoveryHub(), "a2", cache);
            var ra2 = await a2.JoinRoomAsync("Home");
            await TestUtil.WaitFor(() => ra2.Members.Count == 1, 15_000);
            Assert.Equal("b", ra2.Members.Single().DisplayName);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class ConnectionStateTests
{
    [Fact]
    public async Task Check_reports_nobody_found_then_connected_and_peers_are_linked_directly()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        var ra = await a.JoinRoomAsync("Check");

        var alone = await a.CheckNetworkAsync();
        Assert.Contains(alone.Items, i => i.Title == "Nobody else found yet");

        await b.JoinRoomAsync("Check");
        await TestUtil.WaitFor(() => ra.Members.Count == 1);
        var together = await a.CheckNetworkAsync();
        Assert.Contains(together.Items, i => i.Title.StartsWith("Connected to 1 person") && i.Level == CheckLevel.Ok);
        Assert.Equal(PeerLink.Direct, a.GetLink(b.Id));
        Assert.Equal(PeerLink.Offline, a.GetLink(Yaparr.Identity.PeerIdentity.Generate().Id));
    }
}
