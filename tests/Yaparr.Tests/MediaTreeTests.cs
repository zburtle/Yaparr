using System.Collections.Concurrent;
using System.Text;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;
using Yaparr.Transport;
using Yaparr.Voice;

namespace Yaparr.Tests;

public class MediaTreeTests
{
    internal sealed class Cluster : IDisposable
    {
        public readonly InMemoryDiscoveryHub Hub = new();
        public readonly List<ChatNode> Nodes = new();
        public readonly List<ChatRoom> Rooms = new();

        public static async Task<Cluster> StartAsync(int count, int fanout, int rootFanout, Func<int, bool>? capable = null, string room = "stage")
        {
            var c = new Cluster();
            for (int i = 0; i < count; i++)
                c.Nodes.Add(TestUtil.NewNode(c.Hub, "n" + i, relayCapable: capable?.Invoke(i) ?? true, media: MediaOptions.Fast(fanout, rootFanout)));
            foreach (var node in c.Nodes) c.Rooms.Add(await node.JoinRoomAsync(room));
            await TestUtil.WaitFor(() => c.Rooms.All(r => r.Members.Count >= 1), 20_000);
            return c;
        }

        public ChatNode Root => Nodes[0];

        public async Task<List<MediaView>> WatchAllAsync(string group)
        {
            await TestUtil.WaitFor(() => Rooms.Skip(1).All(r => r.MediaGroups.Any(g => g.Id == group)), 20_000);
            var views = new List<MediaView>();
            for (int i = 1; i < Nodes.Count; i++) views.Add(Nodes[i].WatchMedia(Rooms[i], group));
            await TestUtil.WaitFor(() => views.All(v => v.Status.Attached), 30_000);
            return views;
        }

        public void Dispose()
        {
            foreach (var n in Nodes) { try { n.Dispose(); } catch (ObjectDisposedException) { } }
        }
    }

    internal static ConcurrentQueue<string> Collect(MediaView view)
    {
        var got = new ConcurrentQueue<string>();
        view.Received += p => got.Enqueue(Encoding.UTF8.GetString(p.Payload.Span));
        return got;
    }

    internal static async Task SendRange(MediaSource source, int from, int to, int gapMs = 20)
    {
        for (int i = from; i < to; i++)
        {
            source.Send(Encoding.UTF8.GetBytes("p" + i));
            await Task.Delay(gapMs);
        }
    }

    [Fact]
    public async Task A_stream_reaches_every_viewer_through_a_tree_when_each_member_can_only_send_to_two()
    {
        using var c = await Cluster.StartAsync(10, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "alice's screen");
        var views = new List<MediaView>();
        try
        {
            var all = await c.WatchAllAsync(source.Info.Id);
            views.AddRange(all);
            var received = all.Select(Collect).ToList();

            // The shape: nobody has more than two children, everyone's chain of parents ends at the sharer, and it is deeper than one step.
            var parents = new Dictionary<PeerId, PeerId?>();
            for (int i = 1; i < c.Nodes.Count; i++) parents[c.Nodes[i].Id] = all[i - 1].Status.Parent;
            Assert.True(source.Status.Children.Count <= 2);
            Assert.All(all, v => Assert.True(v.Status.Children.Count <= 2));
            Assert.Contains(parents.Values, p => p != c.Root.Id);
            foreach (var start in parents.Keys)
            {
                var cur = start;
                for (int steps = 0; cur != c.Root.Id; steps++)
                {
                    Assert.True(steps < 12, "the chain of parents loops");
                    cur = parents[cur]!.Value;
                }
            }

            await SendRange(source, 0, 30);
            await TestUtil.WaitFor(() => received.All(q => q.Count >= 27), 15_000);
            Assert.All(received, q => Assert.True(q.Distinct().Count() == q.Count, "a packet was delivered twice"));
            Assert.True(c.Root.RelayStats.ForwardedFrames == 0, "stream packets must not use the member-relay counters");
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task A_member_whose_connection_cannot_relay_is_never_made_a_parent()
    {
        using var c = await Cluster.StartAsync(9, fanout: 2, rootFanout: 2, capable: i => i is 0 or > 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen");
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            for (int i = 1; i <= 3; i++) Assert.Empty(views[i - 1].Status.Children);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task When_a_member_in_the_middle_leaves_the_viewers_under_it_find_another_parent_and_keep_receiving()
    {
        using var c = await Cluster.StartAsync(8, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen");
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            var received = views.Select(Collect).ToList();
            int interior = views.FindIndex(v => v.Status.Children.Count > 0);
            Assert.True(interior >= 0, "expected someone to have children");
            var orphans = views[interior].Status.Children.Select(id => c.Nodes.FindIndex(n => n.Id == id)).ToList();

            c.Nodes[interior + 1].Dispose();

            // Keep sending; the orphans must start receiving again once they are re-attached.
            using var stop = new CancellationTokenSource();
            int counter = 0;
            var sender = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested) { source.Send(Encoding.UTF8.GetBytes("t" + Interlocked.Increment(ref counter))); await Task.Delay(30); }
            });
            int mark = Volatile.Read(ref counter);
            try
            {
                await TestUtil.WaitFor(() => orphans.All(o => received[o - 1].Any(p => int.Parse(p[1..]) > mark + 20)), 30_000);
                Assert.All(orphans, o => Assert.True(views[o - 1].Status.Attached));
            }
            finally { stop.Cancel(); await sender; }
        }
        finally { foreach (var v in views.Where((_, i) => true)) { try { v.Dispose(); } catch (ObjectDisposedException) { } } }
    }

    [Fact]
    public async Task A_member_passing_the_stream_on_cannot_change_it_or_add_to_it()
    {
        using var c = await Cluster.StartAsync(7, fanout: 2, rootFanout: 1);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", signed: true);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            var received = views.Select(Collect).ToList();
            int middle = views.FindIndex(v => v.Status.Children.Count > 0);
            int child = c.Nodes.FindIndex(n => n.Id == views[middle].Status.Children[0]) - 1;

            // The middle member writes its own packet in the sharer's name, and one of its own, straight to its child.
            var tag = MediaWire.TagOf(source.Info.Id);
            using var middleIdentity = PeerIdentity.Generate();   // not the sharer's key
            var forged = MediaWire.Pack(tag, MediaWire.Origin8(c.Root.Id), 9000, 8, false, Encoding.UTF8.GetBytes("forged"), middleIdentity);
            var unsigned = MediaWire.Pack(tag, MediaWire.Origin8(c.Nodes[middle + 1].Id), 9001, 8, false, Encoding.UTF8.GetBytes("added"), null);
            var link = c.Nodes[middle + 1].Network.Get(c.Nodes[child + 1].Id)!;
            link.SendUnreliable(ChannelIds.Media, forged);
            link.SendUnreliable(ChannelIds.Media, unsigned);

            await SendRange(source, 0, 10);
            await TestUtil.WaitFor(() => received[child].Count >= 9, 15_000);
            await Task.Delay(300);
            Assert.DoesNotContain("forged", received[child]);
            Assert.DoesNotContain("added", received[child]);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task In_a_one_way_stream_only_the_sharer_can_be_the_source()
    {
        using var c = await Cluster.StartAsync(5, fanout: 2, rootFanout: 1);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", signed: false, upstream: false);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            var received = views.Select(Collect).ToList();
            int middle = views.FindIndex(v => v.Status.Children.Count > 0);
            int child = c.Nodes.FindIndex(n => n.Id == views[middle].Status.Children[0]) - 1;
            var packet = MediaWire.Pack(MediaWire.TagOf(source.Info.Id), MediaWire.Origin8(c.Nodes[middle + 1].Id), 5, 8, false, Encoding.UTF8.GetBytes("mine"), null);
            c.Nodes[middle + 1].Network.Get(c.Nodes[child + 1].Id)!.SendUnreliable(ChannelIds.Media, packet);

            await SendRange(source, 0, 5);
            await TestUtil.WaitFor(() => received[child].Count >= 5, 15_000);
            Assert.DoesNotContain("mine", received[child]);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task Someone_who_starts_watching_later_is_given_the_stream_from_the_last_key_without_waiting()
    {
        using var c = await Cluster.StartAsync(4, fanout: 2, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen");
        var first = await Task.WhenAll(Enumerable.Range(1, 2).Select(async i =>
        {
            await TestUtil.WaitFor(() => c.Rooms[i].MediaGroups.Count == 1, 20_000);
            return c.Nodes[i].WatchMedia(c.Rooms[i], source.Info.Id);
        }));
        await TestUtil.WaitFor(() => first.All(v => v.Status.Attached), 20_000);

        source.Send(Encoding.UTF8.GetBytes("old-picture"), isKey: false);
        source.Send(Encoding.UTF8.GetBytes("KEY"), isKey: true);
        source.Send(Encoding.UTF8.GetBytes("delta1"));
        source.Send(Encoding.UTF8.GetBytes("delta2"));
        await Task.Delay(500);

        await TestUtil.WaitFor(() => c.Rooms[3].MediaGroups.Count == 1, 20_000);
        using var late = c.Nodes[3].WatchMedia(c.Rooms[3], source.Info.Id);
        var got = Collect(late);
        try
        {
            await TestUtil.WaitFor(() => late.Status.Attached && got.Count >= 3, 15_000);
            Assert.Equal(new[] { "KEY", "delta1", "delta2" }, got.Take(3).ToArray());
        }
        finally { foreach (var v in first) v.Dispose(); }
    }

    [Fact]
    public async Task In_a_two_way_stream_every_member_hears_every_other_member_but_not_themselves()
    {
        using var c = await Cluster.StartAsync(7, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "chatter", "two-way", signed: false, upstream: true);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            var received = views.Select(Collect).ToList();
            // Send from two members at different places in the tree.
            var senders = new[] { 1, 6 };
            for (int n = 0; n < 5; n++)
            {
                foreach (var index in senders)
                    c.Nodes[index].PublishMedia(c.Rooms[index].Media[source.Info.Id], Encoding.UTF8.GetBytes($"from{index}-{n}"), false);
                await Task.Delay(40);
            }

            await TestUtil.WaitFor(() => received.Where((_, i) => i + 1 != 1 && i + 1 != 6).All(q => q.Count(p => p.StartsWith("from1-")) >= 4 && q.Count(p => p.StartsWith("from6-")) >= 4), 15_000);
            Assert.DoesNotContain(received[0], p => p.StartsWith("from1-"));
            Assert.DoesNotContain(received[5], p => p.StartsWith("from6-"));
            Assert.True(received[0].Count(p => p.StartsWith("from6-")) >= 4);
            Assert.True(received[5].Count(p => p.StartsWith("from1-")) >= 4);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task Ending_the_stream_removes_it_for_everyone()
    {
        using var c = await Cluster.StartAsync(5, fanout: 2, rootFanout: 2);
        var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen");
        var views = await c.WatchAllAsync(source.Info.Id);
        foreach (var v in views) v.Dispose();
        source.Dispose();
        await TestUtil.WaitFor(() => c.Rooms.All(r => r.MediaGroups.Count == 0), 15_000);
    }

    [Fact]
    public void Packets_that_arrive_twice_or_far_too_late_are_used_once_or_not_at_all()
    {
        var window = new SeqWindow();
        Assert.True(window.Accept(10));
        Assert.False(window.Accept(10));
        Assert.True(window.Accept(8));      // late but new
        Assert.False(window.Accept(8));
        Assert.True(window.Accept(200));
        Assert.False(window.Accept(10));    // now more than 64 behind

        var wrapping = new SeqWindow();
        Assert.True(wrapping.Accept(uint.MaxValue - 1));
        Assert.True(wrapping.Accept(uint.MaxValue));
        Assert.True(wrapping.Accept(1));    // the numbers wrapped around
        Assert.True(wrapping.Accept(0));    // late, but new
        Assert.False(wrapping.Accept(uint.MaxValue));
    }
}

[Collection("real-time audio")]
public class VoiceTreeTests
{
    private static float[] Tone(double hz, int samples, int offset) =>
        Enumerable.Range(0, samples).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * hz * (i + offset) / 48000))).ToArray();

    [Fact]
    public async Task A_big_voice_channel_switches_to_a_tree_and_everyone_can_still_be_heard()
    {
        const int n = 10;
        var hub = new InMemoryDiscoveryHub();
        var nodes = new List<ChatNode>();
        var rooms = new List<ChatRoom>();
        var sessions = new List<VoiceSession>();
        try
        {
            for (int i = 0; i < n; i++)
                nodes.Add(TestUtil.NewNode(hub, "v" + i, media: new MediaOptions
                {
                    Maintenance = TimeSpan.FromMilliseconds(200), EdgeTimeout = TimeSpan.FromMilliseconds(1500), Announce = TimeSpan.FromSeconds(2),
                    VoiceThreshold = 6, VoiceRootFanout = 3, Fanout = 2, VoiceRankDelay = TimeSpan.FromMilliseconds(300),
                }));
            foreach (var node in nodes) rooms.Add(await node.JoinRoomAsync("big"));
            await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count >= 1), 20_000);
            foreach (var room in rooms)
            {
                var s = room.JoinVoice();
                s.Activation = VoiceActivation.Open;
                sessions.Add(s);
            }
            await TestUtil.WaitFor(() => rooms.All(r => r.VoiceParticipants.Count == n), 30_000);

            // Somebody starts the tree and everybody attaches to it.
            await TestUtil.WaitFor(() => rooms.All(r => r.Media.Values.Any(g => g.Kind == "voice" && g.Attached && g.Joined)), 40_000);
            var groups = rooms.Select(r => r.Media.Values.Single(g => g.Kind == "voice")).ToList();
            Assert.Single(groups.Select(g => g.Id).Distinct());
            Assert.Single(groups, g => g.IsRoot);
            Assert.All(groups, g => Assert.True(g.Children.Count <= 3));

            // One person talks; the rest listen at different places in the tree.
            int speaker = groups.FindIndex(g => !g.IsRoot && g.Children.Count == 0);
            var listeners = Enumerable.Range(0, n).Where(i => i != speaker).ToList();
            var heard = listeners.ToDictionary(i => i, _ => new List<float>());
            var buffer = new float[480];
            int offset = 0;
            long next = Environment.TickCount64;
            for (int tick = 0; tick < 300; tick++)
            {
                sessions[speaker].PushCapture(Tone(440, 480, offset)); offset += 480;
                foreach (var i in listeners) { sessions[i].PullPlayback(buffer); heard[i].AddRange(buffer); }
                next += 10;
                long wait = next - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait);
            }

            foreach (var i in listeners)
            {
                int loud = 0;
                var samples = heard[i];
                for (int f = 0; f + 480 <= samples.Count; f += 480)
                    if (samples.GetRange(f, 480).Max(Math.Abs) > 0.05f) loud++;
                Assert.True(loud >= 50, $"listener {i} heard only {loud} loud frames");
            }
            Assert.True(groups.Sum(g => g.Forwarded) > 0, "the audio should have been passed on by members of the tree");
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
            foreach (var node in nodes) node.Dispose();
        }
    }

    [Fact]
    public async Task When_the_root_of_a_voice_tree_disappears_another_member_starts_a_new_tree_and_audio_flows_again()
    {
        const int n = 8;
        var hub = new InMemoryDiscoveryHub();
        var nodes = new List<ChatNode>();
        var rooms = new List<ChatRoom>();
        var sessions = new List<VoiceSession>();
        try
        {
            for (int i = 0; i < n; i++)
                nodes.Add(TestUtil.NewNode(hub, "v" + i, media: new MediaOptions
                {
                    Maintenance = TimeSpan.FromMilliseconds(200), EdgeTimeout = TimeSpan.FromMilliseconds(1200), Announce = TimeSpan.FromSeconds(2),
                    VoiceThreshold = 5, VoiceRootFanout = 3, Fanout = 2, VoiceRankDelay = TimeSpan.FromMilliseconds(300),
                }));
            foreach (var node in nodes) rooms.Add(await node.JoinRoomAsync("failover"));
            await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count >= 1), 20_000);
            foreach (var room in rooms) { var s = room.JoinVoice(); s.Activation = VoiceActivation.Open; sessions.Add(s); }
            await TestUtil.WaitFor(() => rooms.All(r => r.VoiceParticipants.Count == n), 30_000);
            await TestUtil.WaitFor(() => rooms.All(r => r.Media.Values.Any(g => g.Kind == "voice" && g.Attached && g.Joined)), 40_000);

            int rootIndex = rooms.FindIndex(r => r.Media.Values.Any(g => g.Kind == "voice" && g.IsRoot));
            string firstGroup = rooms[rootIndex].Media.Values.Single(g => g.Kind == "voice").Id;
            sessions[rootIndex].Dispose();
            nodes[rootIndex].Dispose();

            var alive = Enumerable.Range(0, n).Where(i => i != rootIndex).ToList();
            await TestUtil.WaitFor(() => alive.All(i => rooms[i].Media.Values.Any(g => g.Kind == "voice" && g.Id != firstGroup && g.Attached && g.Joined)), 60_000);
            Assert.Single(alive.Select(i => rooms[i].Media.Values.Single(g => g.Kind == "voice" && g.Id != firstGroup && g.Joined).Id).Distinct());

            int speaker = alive[0];
            var listeners = alive.Skip(1).ToList();
            var heard = listeners.ToDictionary(i => i, _ => new List<float>());
            var buffer = new float[480];
            int offset = 0;
            long next = Environment.TickCount64;
            for (int tick = 0; tick < 300; tick++)
            {
                sessions[speaker].PushCapture(Tone(440, 480, offset)); offset += 480;
                foreach (var i in listeners) { sessions[i].PullPlayback(buffer); heard[i].AddRange(buffer); }
                next += 10;
                long wait = next - Environment.TickCount64;
                if (wait > 0) await Task.Delay((int)wait);
            }
            foreach (var i in listeners)
            {
                int loud = 0;
                for (int f = 0; f + 480 <= heard[i].Count; f += 480)
                    if (heard[i].GetRange(f, 480).Max(Math.Abs) > 0.05f) loud++;
                Assert.True(loud >= 40, $"listener {i} heard only {loud} loud frames after the root left");
            }
        }
        finally
        {
            foreach (var s in sessions) { try { s.Dispose(); } catch (ObjectDisposedException) { } }
            foreach (var node in nodes) { try { node.Dispose(); } catch (ObjectDisposedException) { } }
        }
    }

    [Fact]
    public async Task A_small_channel_keeps_using_direct_connections()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a", media: MediaOptions.Fast());
        using var b = TestUtil.NewNode(hub, "b", media: MediaOptions.Fast());
        var ra = await a.JoinRoomAsync("small");
        var rb = await b.JoinRoomAsync("small");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);
        using var va = ra.JoinVoice();
        using var vb = rb.JoinVoice();
        await TestUtil.WaitFor(() => ra.VoiceParticipants.Count == 2 && rb.VoiceParticipants.Count == 2);
        await Task.Delay(3000);
        Assert.Empty(ra.Media);
        Assert.Empty(rb.Media);
    }
}
