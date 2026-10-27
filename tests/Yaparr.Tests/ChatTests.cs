using System.Net;
using System.Collections.Concurrent;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class ChatTests
{
    [Fact]
    public async Task Direct_chat_is_found_by_peer_id_and_exchanges_messages()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        alice.Start(); bob.Start();
        await Task.Delay(100);

        var incoming = new TaskCompletionSource<DirectChat>();
        bob.IncomingDirectChat += c => incoming.TrySetResult(c);
        var bobGot = new TaskCompletionSource<ChatMessage>();
        var aliceGot = new TaskCompletionSource<ChatMessage>();

        var toBob = await alice.OpenDirectChatAsync(bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        toBob.MessageReceived += m => aliceGot.TrySetResult(m);
        await toBob.SendAsync("hello bob");

        var toAlice = await incoming.Task.WaitAsync(TimeSpan.FromSeconds(5));
        toAlice.MessageReceived += m => bobGot.TrySetResult(m);
        Assert.Equal(alice.Id, toAlice.PeerId);

        await toBob.SendAsync("again");
        var m1 = await bobGot.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(m1.Text, new[] { "hello bob", "again" });
        Assert.Equal("alice", m1.From.DisplayName);
        Assert.Null(m1.Room);

        await toAlice.SendAsync("hi alice");
        Assert.Equal("hi alice", (await aliceGot.Task.WaitAsync(TimeSpan.FromSeconds(5))).Text);
    }

    [Fact]
    public async Task Group_chat_forms_a_full_mesh_and_delivers_to_all_members()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        using var c = TestUtil.NewNode(hub, "c");

        var inbox = new ConcurrentDictionary<string, ConcurrentBag<string>>();
        ChatRoom Wire(ChatNode n, ChatRoom r)
        {
            inbox[n.DisplayName] = new ConcurrentBag<string>();
            r.MessageReceived += m => inbox[n.DisplayName].Add($"{m.From.DisplayName}:{m.Text}");
            return r;
        }

        var ra = Wire(a, await a.JoinRoomAsync("Lobby"));
        var rb = Wire(b, await b.JoinRoomAsync("lobby "));
        var rc = Wire(c, await c.JoinRoomAsync("LOBBY"));

        await TestUtil.WaitFor(() => ra.Members.Count == 2 && rb.Members.Count == 2 && rc.Members.Count == 2);

        await ra.SendAsync("hello all");
        await TestUtil.WaitFor(() => inbox["b"].Count == 1 && inbox["c"].Count == 1);
        Assert.Contains("a:hello all", inbox["b"]);
        Assert.Contains("a:hello all", inbox["c"]);
        Assert.Empty(inbox["a"]);

        await rc.SendAsync("from c");
        await TestUtil.WaitFor(() => inbox["a"].Count == 1 && inbox["b"].Count == 2);
    }

    [Fact]
    public async Task Rooms_are_isolated_and_leaving_stops_delivery()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        using var c = TestUtil.NewNode(hub, "c");

        var ra = await a.JoinRoomAsync("one");
        var rb = await b.JoinRoomAsync("one");
        var rc = await c.JoinRoomAsync("two");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);

        int cGot = 0, bGot = 0;
        rc.MessageReceived += _ => cGot++;
        rb.MessageReceived += _ => bGot++;
        await ra.SendAsync("only room one");
        await TestUtil.WaitFor(() => bGot == 1);
        Assert.Equal(0, cGot);

        await rb.LeaveAsync();
        await TestUtil.WaitFor(() => ra.Members.Count == 0);
        await ra.SendAsync("nobody home");
        await Task.Delay(300);
        Assert.Equal(1, bGot);
    }
}

public class PrivateRoomTests
{
    [Fact]
    public void Invite_strings_round_trip_and_topics_are_unguessable()
    {
        var key = RoomKey.Generate();
        var parsed = RoomKey.Parse(key.ToString());
        Assert.Equal(key.ToString(), parsed.ToString());
        Assert.Equal(key.Topic, parsed.Topic);
        Assert.NotEqual(key.Topic, RoomKey.Generate().Topic);
        Assert.NotEqual(key.Topic, Topics.ForRoom("lobby"));
        Assert.Throws<FormatException>(() => RoomKey.Parse("not-a-key"));
    }

    [Fact]
    public async Task Key_holders_form_a_mesh_and_chat()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        using var c = TestUtil.NewNode(hub, "c");
        var key = RoomKey.Generate();

        var ra = await a.JoinPrivateRoomAsync(key);
        var rb = await b.JoinPrivateRoomAsync(RoomKey.Parse(key.ToString()));
        var rc = await c.JoinPrivateRoomAsync(key);
        await TestUtil.WaitFor(() => ra.Members.Count == 2 && rb.Members.Count == 2 && rc.Members.Count == 2);

        var got = new TaskCompletionSource<string>();
        rc.MessageReceived += m => got.TrySetResult(m.Text);
        await ra.SendAsync("secret hello");
        Assert.Equal("secret hello", await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(ra.IsPrivate);
    }

    [Fact]
    public async Task Wrong_key_peer_connected_directly_is_never_a_member()
    {
        var hub = new InMemoryDiscoveryHub();
        using var member1 = TestUtil.NewNode(hub, "m1");
        using var member2 = TestUtil.NewNode(hub, "m2");
        using var intruder = TestUtil.NewNode(hub, "intruder");
        var key = RoomKey.Generate();

        var r1 = await member1.JoinPrivateRoomAsync(key);
        var r2 = await member2.JoinPrivateRoomAsync(key);
        await TestUtil.WaitFor(() => r1.Members.Count == 1 && r2.Members.Count == 1);

        var wrong = await intruder.JoinPrivateRoomAsync(RoomKey.Generate());
        var guessed = await intruder.JoinRoomAsync("lobby");
        await intruder.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, member1.LocalPort));
        await TestUtil.WaitFor(() => member1.ConnectedPeers.Count == 2);
        await Task.Delay(1000);

        Assert.Equal(1, r1.Members.Count);
        Assert.Empty(wrong.Members);
        Assert.Empty(guessed.Members);

        int leaked = 0;
        wrong.MessageReceived += _ => leaked++;
        guessed.MessageReceived += _ => leaked++;
        intruder.IncomingDirectChat += _ => leaked++;
        await r1.SendAsync("members only");
        await Task.Delay(500);
        Assert.Equal(0, leaked);

        int injected = 0;
        r1.MessageReceived += _ => injected++;
        Assert.Empty(wrong.GetHistory());
        Assert.Empty(guessed.GetHistory());
        await wrong.SendAsync("let me in");
        await guessed.SendAsync("let me in");
        await Task.Delay(500);
        Assert.Equal(0, injected);
    }
}

public class RelayTests
{
    private static void Isolate(ChatNode x, ChatNode y)
    {
        x.Mux.OutboundFilter = (_, to) => to.Port != y.LocalPort;
        y.Mux.OutboundFilter = (_, to) => to.Port != x.LocalPort;
    }

    [Fact]
    public async Task Peers_that_cannot_reach_each_other_chat_through_a_mutual_member_and_upgrade_when_possible()
    {
        var hub = new InMemoryDiscoveryHub();
        using var relay = TestUtil.NewNode(hub, "relay");
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);

        var rr = await relay.JoinRoomAsync("mesh");
        var ra = await a.JoinRoomAsync("mesh");
        var rb = await b.JoinRoomAsync("mesh");

        await TestUtil.WaitFor(() => a.Network.Get(b.Id) != null && b.Network.Get(a.Id) != null && rr.Members.Count == 2);
        Assert.True(a.Network.Get(b.Id)!.IsRelayed);
        Assert.True(b.Network.Get(a.Id)!.IsRelayed);
        Assert.True(ra.Members.Single(m => m.Id == b.Id).IsRelayed);

        var got = new TaskCompletionSource<string>();
        rb.MessageReceived += m => { if (m.From.Id == a.Id) got.TrySetResult(m.Text); };
        var big = new string('x', 5000);
        await ra.SendAsync(big);
        Assert.Equal(big, await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        var back = new TaskCompletionSource<string>();
        ra.MessageReceived += m => { if (m.From.Id == b.Id) back.TrySetResult(m.Text); };
        await rb.SendAsync("pong");
        Assert.Equal("pong", await back.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        a.Mux.OutboundFilter = null; b.Mux.OutboundFilter = null;
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: false } && b.Network.Get(a.Id) is { IsRelayed: false }, 15_000);
        var after = new TaskCompletionSource<string>();
        rb.MessageReceived += m => { if (m.Text == "direct now") after.TrySetResult(m.Text); };
        await TestUtil.WaitFor(() => ra.Members.Count == 2);
        await ra.SendAsync("direct now");
        await after.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Without_relaying_enabled_isolated_peers_stay_disconnected()
    {
        var hub = new InMemoryDiscoveryHub();
        using var relay = TestUtil.NewNode(hub, "relay", relaying: false);
        using var a = TestUtil.NewNode(hub, "a", relaying: false);
        using var b = TestUtil.NewNode(hub, "b", relaying: false);
        Isolate(a, b);

        var rr = await relay.JoinRoomAsync("mesh");
        var ra = await a.JoinRoomAsync("mesh");
        var rb = await b.JoinRoomAsync("mesh");
        await TestUtil.WaitFor(() => rr.Members.Count == 2);
        await Task.Delay(2500);
        Assert.Null(a.Network.Get(b.Id));
        Assert.Null(b.Network.Get(a.Id));
    }

    [Fact]
    public async Task Group_messages_are_forwarded_by_members_between_peers_with_no_connection()
    {
        var hub = new InMemoryDiscoveryHub();
        using var hubNode = TestUtil.NewNode(hub, "hub", relaying: false);
        using var a = TestUtil.NewNode(hub, "a", relaying: false);
        using var c = TestUtil.NewNode(hub, "c", relaying: false);
        Isolate(a, c);

        var rh = await hubNode.JoinRoomAsync("mesh");
        var ra = await a.JoinRoomAsync("mesh");
        var rc = await c.JoinRoomAsync("mesh");

        await TestUtil.WaitFor(() => rh.Members.Count == 2 && ra.Members.Count == 2 && rc.Members.Count == 2);
        Assert.Null(a.Network.Get(c.Id));
        Assert.Contains(ra.Members, m => m.Id == c.Id);

        var atC = new TaskCompletionSource<ChatMessage>();
        var atA = new TaskCompletionSource<ChatMessage>();
        rc.MessageReceived += m => { if (m.Text == "via the hub") atC.TrySetResult(m); };
        ra.MessageReceived += m => { if (m.Text == "and back") atA.TrySetResult(m); };

        await ra.SendAsync("via the hub");
        var received = await atC.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(a.Id, received.From.Id);
        Assert.False(received.IsCatchUp);

        await rc.SendAsync("and back");
        Assert.Equal(c.Id, (await atA.Task.WaitAsync(TimeSpan.FromSeconds(10))).From.Id);
    }

    [Fact]
    public async Task Forwarding_never_goes_through_a_node_that_is_not_in_the_group()
    {
        var hub = new InMemoryDiscoveryHub();
        using var outsider = TestUtil.NewNode(hub, "outsider", relaying: false);
        using var a = TestUtil.NewNode(hub, "a", relaying: false);
        using var c = TestUtil.NewNode(hub, "c", relaying: false);
        Isolate(a, c);

        await outsider.JoinRoomAsync("other");
        await a.JoinRoomAsync("other");
        await c.JoinRoomAsync("other");
        var ra = await a.JoinRoomAsync("mesh");
        var rc = await c.JoinRoomAsync("mesh");

        await TestUtil.WaitFor(() => a.Network.Get(outsider.Id) != null && c.Network.Get(outsider.Id) != null);
        int received = 0;
        rc.MessageReceived += _ => Interlocked.Increment(ref received);
        await Task.Delay(1000);
        await ra.SendAsync("only for the group");
        await Task.Delay(2500);

        Assert.Equal(0, received);
        Assert.Empty(ra.Members);
        Assert.Empty(rc.Members);
    }

    [Fact]
    public async Task A_relay_that_does_not_share_a_room_with_both_peers_refuses_to_forward()
    {
        var hub = new InMemoryDiscoveryHub();
        using var relay = TestUtil.NewNode(hub, "relay");
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);

        var rr1 = await relay.JoinRoomAsync("one");
        var rr2 = await relay.JoinRoomAsync("two");
        var ra = await a.JoinRoomAsync("one");
        var rb = await b.JoinRoomAsync("two");
        await TestUtil.WaitFor(() => rr1.Members.Count == 1 && rr2.Members.Count == 1);

        await TestUtil.WaitFor(() => a.Network.Get(relay.Id) != null && b.Network.Get(relay.Id) != null);
        var fromA = a.Network.ConnectVia(a.Network.Get(relay.Id)!, b.Id);
        var fromB = b.Network.ConnectVia(b.Network.Get(relay.Id)!, a.Id);
        await Task.Delay(2000);
        Assert.Equal(ConnectionState.Connecting, fromA.State);
        Assert.Equal(ConnectionState.Connecting, fromB.State);
    }
}

public class HistoryTests
{
    private static ChatNode NodeWith(InMemoryDiscoveryHub hub, string name, ChatNodeOptions? basis = null, PeerIdentity? identity = null,
        IChatHistoryStore? store = null, bool sync = true) => new(new ChatNodeOptions
        {
            DisplayName = name,
            Identity = identity,
            HistoryStore = store,
            SyncHistory = sync,
            DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
            DiscoveryInterval = TimeSpan.FromMilliseconds(300),
            Network = TestUtil.FastNetwork,
        });

    [Fact]
    public async Task A_member_who_was_offline_catches_up_on_missed_messages()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = NodeWith(hub, "a");
        using var c = NodeWith(hub, "c");
        var b = NodeWith(hub, "b");
        var ra = await a.JoinRoomAsync("lounge");
        var rc = await c.JoinRoomAsync("lounge");
        var rb = await b.JoinRoomAsync("lounge");
        await TestUtil.WaitFor(() => ra.Members.Count == 2 && rb.Members.Count == 2 && rc.Members.Count == 2);

        await ra.SendAsync("before");
        await TestUtil.WaitFor(() => rb.GetHistory().Count == 1);

        b.Dispose();
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rc.Members.Count == 1);

        await ra.SendAsync("while away 1");
        await ra.SendAsync("while away 2");
        await Task.Delay(5);
        await rc.SendAsync("while away 3");

        using var b2 = NodeWith(hub, "b");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<ChatMessage>();
        var rb2 = await b2.JoinRoomAsync("lounge");
        rb2.MessageReceived += seen.Enqueue;

        await TestUtil.WaitFor(() => rb2.GetHistory().Count == 4);
        var history = rb2.GetHistory();
        Assert.Equal(new[] { "before", "while away 1", "while away 2", "while away 3" }, history.Select(m => m.Text));
        Assert.Equal(new[] { a.Id, a.Id, a.Id, c.Id }, history.Select(m => m.From.Id));
        Assert.Equal(new[] { "a", "a", "a", "c" }, history.Select(m => m.From.DisplayName));
        Assert.All(seen, m => Assert.True(m.IsCatchUp));

        var live = new TaskCompletionSource<ChatMessage>();
        rb2.MessageReceived += m => { if (m.Text == "live") live.TrySetResult(m); };
        await ra.SendAsync("live");
        Assert.False((await live.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsCatchUp);
        await Task.Delay(300);
        Assert.Equal(5, rb2.GetHistory().Count);
    }

    [Fact]
    public async Task History_persists_in_a_file_store_across_restarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new InMemoryDiscoveryHub();
            using var identity = PeerIdentity.Generate();
            using (var first = NodeWith(hub, "me", identity: identity, store: new FileHistoryStore(dir)))
            {
                var room = await first.JoinRoomAsync("notes");
                await room.SendAsync("remember this");
            }

            using var second = NodeWith(new InMemoryDiscoveryHub(), "me", identity: identity, store: new FileHistoryStore(dir));
            var again = await second.JoinRoomAsync("notes");
            var m = Assert.Single(again.GetHistory());
            Assert.Equal("remember this", m.Text);
            Assert.Equal(identity.Id, m.From.Id);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Sync_can_be_disabled()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = NodeWith(hub, "a", sync: false);
        var ra = await a.JoinRoomAsync("quiet");
        await ra.SendAsync("old news");

        using var b = NodeWith(hub, "b", sync: false);
        var rb = await b.JoinRoomAsync("quiet");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);
        await Task.Delay(500);
        Assert.Empty(rb.GetHistory());
    }

    [Fact]
    public void Signatures_reject_tampering_wrong_rooms_and_borrowed_keys()
    {
        using var alice = PeerIdentity.Generate();
        using var mallory = PeerIdentity.Generate();
        var m = SignedMessage.Create(alice, "room1", "alice", "hello");

        Assert.True(SignedMessage.Verify(m, "room1"));
        Assert.False(SignedMessage.Verify(m with { Text = "HELLO" }, "room1"));
        Assert.False(SignedMessage.Verify(m with { Name = "bob" }, "room1"));
        Assert.False(SignedMessage.Verify(m with { Ts = m.Ts + 1 }, "room1"));
        Assert.False(SignedMessage.Verify(m, "room2"));
        Assert.False(SignedMessage.Verify(m with { Pub = Convert.ToBase64String(mallory.PublicKey) }, "room1"));
        var forged = SignedMessage.Create(mallory, "room1", "alice", "hello") with { From = alice.Id.Value };
        Assert.False(SignedMessage.Verify(forged, "room1"));
        Assert.False(SignedMessage.Verify(m with { Ts = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }, "room1"));
    }

    [Fact]
    public void History_is_bounded_by_count_and_age_and_deduplicates()
    {
        using var id = PeerIdentity.Generate();
        var history = new RoomHistory("r", null, max: 3, maxAge: TimeSpan.FromDays(1));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var msgs = Enumerable.Range(0, 5).Select(i => SignedMessage.Create(id, "r", "x", $"m{i}") with { Ts = now - (5 - i) * 1000 }).ToList();
        foreach (var m in msgs) Assert.True(history.TryAdd(m));
        Assert.False(history.TryAdd(msgs[4]));

        Assert.Equal(new[] { "m2", "m3", "m4" }, history.Snapshot().Select(m => m.Text));

        var ancient = SignedMessage.Create(id, "r", "x", "old") with { Ts = now - (long)TimeSpan.FromDays(2).TotalMilliseconds };
        Assert.False(history.TryAdd(ancient));

        var have = new HashSet<string> { SignedMessage.Key(msgs[3]) };
        Assert.Equal(new[] { "m2", "m4" }, history.Missing(have, 10).Select(m => m.Text));
    }
}

public class DiscoveryTests
{
    private sealed class FailingDiscovery : IPeerDiscovery
    {
        public Task AnnounceAsync(byte[] topic, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<System.Net.IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default) => throw new InvalidOperationException("boom");
        public void Dispose() { }
    }

    private sealed class DualAddressDiscovery(System.Collections.Concurrent.ConcurrentDictionary<int, byte> ports, int self) : IPeerDiscovery
    {
        public Task AnnounceAsync(byte[] topic, CancellationToken ct = default) { ports[self] = 0; return Task.CompletedTask; }
        public Task<IReadOnlyList<System.Net.IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<System.Net.IPEndPoint>>(ports.Keys.Where(p => p != self).SelectMany(p => new[]
            {
                new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, p),
                new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.2"), p),
            }).ToList());
        public void Dispose() { }
    }

    [Fact]
    public async Task Composite_skips_members_that_fail()
    {
        var hub = new InMemoryDiscoveryHub();
        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 4000);
        var composite = new CompositeDiscovery(new[] { new FailingDiscovery(), hub.CreateClient(ep) });
        var topic = Topics.ForRoom("x");
        await composite.AnnounceAsync(topic);
        Assert.Contains(ep, await composite.FindPeersAsync(topic));
    }

    [Fact]
    public async Task Local_service_discovery_finds_peers_on_the_lan()
    {
        if (!System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.SupportsMulticast &&
                n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback))
            return;

        using var a = new LocalServiceDiscovery(41001);
        using var b = new LocalServiceDiscovery(41002);
        var topic = Topics.ForRoom("lan-" + Guid.NewGuid());

        await a.AnnounceAsync(topic);
        await TestUtil.WaitFor(() => b.FindPeersAsync(topic).Result.Any(e => e.Port == 41001));

        await b.AnnounceAsync(topic);
        await TestUtil.WaitFor(() => a.FindPeersAsync(topic).Result.Any(e => e.Port == 41002));
        Assert.DoesNotContain(41001, a.FindPeersAsync(topic).Result.Select(e => e.Port));
    }

    [Fact]
    public async Task A_peer_reachable_at_two_addresses_does_not_cause_connection_flapping()
    {
        var ports = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        ChatNode Make(string name) => new(new ChatNodeOptions
        {
            DisplayName = name,
            DiscoveryFactory = mux => new DualAddressDiscovery(ports, mux.LocalPort),
            DiscoveryInterval = TimeSpan.FromMilliseconds(300),
            Network = TestUtil.FastNetwork,
        });
        using var a = Make("a"); using var b = Make("b");

        int dropsA = 0, dropsB = 0;
        a.PeerDisconnected += _ => Interlocked.Increment(ref dropsA);
        b.PeerDisconnected += _ => Interlocked.Increment(ref dropsB);
        var ra = await a.JoinRoomAsync("dual");
        var rb = await b.JoinRoomAsync("dual");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);
        await Task.Delay(1500);
        Interlocked.Exchange(ref dropsA, 0); Interlocked.Exchange(ref dropsB, 0);
        await Task.Delay(3000);

        Assert.Equal(0, dropsA);
        Assert.Equal(0, dropsB);
    }
}

public class LimitedMeshTests
{
    private static ChatNode Node(InMemoryDiscoveryHub hub, string name, int target, int max) => new(new ChatNodeOptions
    {
        DisplayName = name,
        TargetConnections = target,
        MaxConnections = max,
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(400),
        MaintenanceInterval = TimeSpan.FromMilliseconds(200),
        ShuffleInterval = TimeSpan.FromMilliseconds(600),
        PresenceInterval = TimeSpan.FromMilliseconds(500),
        Network = TestUtil.FastNetwork,
    });

    [Fact]
    public async Task A_large_room_stays_within_the_connection_limit_and_still_delivers_to_everyone()
    {
        const int count = 24, target = 4, max = 6;
        var hub = new InMemoryDiscoveryHub();
        var nodes = new List<ChatNode>();
        var rooms = new List<ChatRoom>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                var node = Node(hub, "n" + i, target, max);
                nodes.Add(node);
                rooms.Add(await node.JoinRoomAsync("crowd"));
            }

            await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count == count - 1), 60_000);
            await TestUtil.WaitFor(() => nodes.All(n => n.ConnectedPeers.Count is >= 1 and <= max), 15_000);

            var received = new HashSet<int>();
            for (int i = 1; i < count; i++)
            {
                int index = i;
                rooms[i].MessageReceived += m => { if (m.Text == "hello everyone") lock (received) received.Add(index); };
            }
            await rooms[0].SendAsync("hello everyone");
            await TestUtil.WaitFor(() => { lock (received) return received.Count == count - 1; }, 20_000);

            await TestUtil.WaitFor(() => nodes.All(n => n.ConnectedPeers.Count is >= 1 and <= max), 15_000);
        }
        finally
        {
            foreach (var n in nodes) n.Dispose();
        }
    }

    [Fact]
    public async Task A_member_that_leaves_disappears_from_everyones_list()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = Node(hub, "a", 2, 3);
        using var b = Node(hub, "b", 2, 3);
        var c = Node(hub, "c", 2, 3);
        var ra = await a.JoinRoomAsync("small");
        var rb = await b.JoinRoomAsync("small");
        var rc = await c.JoinRoomAsync("small");
        await TestUtil.WaitFor(() => ra.Members.Count == 2 && rb.Members.Count == 2);

        c.Dispose();
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1, 10_000);
    }
}
