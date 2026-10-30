using System.Collections.Concurrent;
using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class FrameAssemblerTests
{
    private static byte[] Frame(int n, int size) => Enumerable.Range(0, size).Select(i => (byte)(n * 31 + i)).ToArray();

    private static List<byte[]> Cut(uint id, byte[] frame, bool key, int piece = 100)
    {
        int count = (frame.Length + piece - 1) / piece;
        return Enumerable.Range(0, count)
            .Select(i => Fragments.Pack(id, i, count, key, frame.AsSpan(i * piece, Math.Min(piece, frame.Length - i * piece))))
            .ToList();
    }

    private static readonly PeerId Who = PeerIdentity.Generate().Id;

    [Fact]
    public void Fragments_arriving_out_of_order_and_twice_make_one_frame()
    {
        var assembler = new FrameAssembler(250, 0);
        var ready = new List<MediaFrame>();
        var frame = Frame(1, 950);
        var pieces = Cut(1, frame, key: true);
        foreach (var p in pieces.AsEnumerable().Reverse().Concat(pieces.Take(3))) assembler.Add(Who, p, 10, ready);

        var got = Assert.Single(ready);
        Assert.Equal(frame, got.Data.ToArray());
        Assert.True(got.IsKey);
    }

    [Fact]
    public void Frames_before_the_first_key_frame_are_not_handed_over()
    {
        var assembler = new FrameAssembler(250, 0);
        var ready = new List<MediaFrame>();
        foreach (var p in Cut(5, Frame(5, 300), key: false)) assembler.Add(Who, p, 10, ready);
        Assert.Empty(ready);
        foreach (var p in Cut(6, Frame(6, 300), key: true)) assembler.Add(Who, p, 20, ready);
        foreach (var p in Cut(7, Frame(7, 300), key: false)) assembler.Add(Who, p, 30, ready);
        Assert.Equal(new uint[] { 6, 7 }, ready.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void A_frame_that_never_completes_costs_everything_until_the_next_key_frame_and_asks_for_one()
    {
        var assembler = new FrameAssembler(250, 0);
        var ready = new List<MediaFrame>();
        foreach (var p in Cut(1, Frame(1, 300), key: true)) assembler.Add(Who, p, 0, ready);
        Assert.Single(ready);

        // Frame 2 loses its middle piece; frames 3 and 4 are fine but cannot be used without it.
        var two = Cut(2, Frame(2, 500), key: false);
        two.RemoveAt(2);
        foreach (var p in two) assembler.Add(Who, p, 100, ready);
        foreach (var p in Cut(3, Frame(3, 300), key: false)) assembler.Add(Who, p, 110, ready);
        Assert.Single(ready);

        bool asked = assembler.Tick(400, ready);
        Assert.True(asked);
        Assert.True(assembler.WaitingForKey);
        Assert.Single(ready);
        Assert.True(assembler.Lost >= 1);

        foreach (var p in Cut(4, Frame(4, 300), key: false)) assembler.Add(Who, p, 410, ready);
        Assert.Single(ready);
        foreach (var p in Cut(9, Frame(9, 300), key: true)) assembler.Add(Who, p, 420, ready);
        foreach (var p in Cut(10, Frame(10, 300), key: false)) assembler.Add(Who, p, 430, ready);
        Assert.Equal(new uint[] { 1, 9, 10 }, ready.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void A_late_piece_that_arrives_in_time_saves_the_frame()
    {
        var assembler = new FrameAssembler(250, 0);
        var ready = new List<MediaFrame>();
        foreach (var p in Cut(1, Frame(1, 300), key: true)) assembler.Add(Who, p, 0, ready);
        var two = Cut(2, Frame(2, 500), key: false);
        var late = two[2];
        two.RemoveAt(2);
        foreach (var p in two) assembler.Add(Who, p, 100, ready);
        Assert.False(assembler.Tick(200, ready));
        assembler.Add(Who, late, 220, ready);
        Assert.Equal(new uint[] { 1, 2 }, ready.Select(f => f.Id).ToArray());
        Assert.Equal(0, assembler.Lost);
    }
}

public class StreamFrameTests
{
    private static byte[] Frame(int n, int size) => Enumerable.Range(0, size).Select(i => (byte)(n * 31 + i)).ToArray();

    private static ConcurrentQueue<MediaFrame> Watch(MediaView view)
    {
        var frames = new ConcurrentQueue<MediaFrame>();
        view.FrameReceived += f => frames.Enqueue(f);
        return frames;
    }

    // Each frame after a key must be the one after the last; only a key frame may jump.
    private static void AssertDecodable(IEnumerable<MediaFrame> frames)
    {
        uint? previous = null;
        foreach (var f in frames)
        {
            if (!f.IsKey && previous != null) Assert.Equal(previous.Value + 1, f.Id);
            if (!f.IsKey && previous == null) Assert.Fail("the first frame handed over was not a key frame");
            previous = f.Id;
        }
    }

    [Fact]
    public async Task Big_and_small_frames_arrive_whole_and_in_order_at_every_viewer_through_the_tree()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(7, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1500);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            var got = views.Select(Watch).ToList();
            await source.SendFrameAsync(Frame(0, 60_000), isKey: true);
            for (int n = 1; n <= 20; n++)
            {
                await source.SendFrameAsync(Frame(n, 3000), isKey: false);
                await Task.Delay(30);
            }

            await TestUtil.WaitFor(() => got.All(q => q.Count == 21), 20_000);
            foreach (var q in got)
            {
                var frames = q.ToArray();
                AssertDecodable(frames);
                for (int n = 0; n <= 20; n++) Assert.Equal(Frame(n, n == 0 ? 60_000 : 3000), frames[n].Data.ToArray());
                Assert.True(frames[0].IsKey);
            }
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task A_big_frame_is_spread_out_in_time_and_not_sent_as_one_burst()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(3, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            var got = views.Select(Watch).ToList();
            var timer = Stopwatch.StartNew();
            await source.SendFrameAsync(Frame(0, 200_000), isKey: true);   // 500 KB/s for 1000 kbit/s: about 0.4 s
            Assert.InRange(timer.ElapsedMilliseconds, 250, 4000);
            await TestUtil.WaitFor(() => got.All(q => q.Count == 1), 10_000);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    // A chain, so there is exactly one hop to damage: sharer, first, second.
    private static async Task<(MediaTreeTests.Cluster Cluster, MediaSource Source, List<MediaView> Views, int First, int Second)> Chain()
    {
        var c = await MediaTreeTests.Cluster.StartAsync(3, fanout: 1, rootFanout: 1);
        var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        int first = views.FindIndex(v => v.Status.Parent == c.Root.Id) + 1;
        int second = views.FindIndex(v => v.Status.Parent == c.Nodes[first].Id) + 1;
        Assert.True(first > 0 && second > 0);
        return (c, source, views, first, second);
    }

    [Fact]
    public async Task Lost_packets_on_one_hop_are_asked_for_again_and_no_frame_is_lost()
    {
        var (c, source, views, first, second) = await Chain();
        using var cluster = c;
        try
        {
            var got = Watch(views[second - 1]);
            var drop = new Random(7);
            c.Nodes[first].Mux.OutboundFilter = (_, to) => to.Port != c.Nodes[second].LocalPort || drop.NextDouble() > 0.04;

            await source.SendFrameAsync(Frame(0, 20_000), isKey: true);
            for (int n = 1; n <= 80; n++)
            {
                await source.SendFrameAsync(Frame(n, 4000), isKey: false);
                await Task.Delay(25);
            }
            await TestUtil.WaitFor(() => got.Count >= 81, 20_000);

            // Every frame arrives. If a retransmission was unlucky enough to miss its time, the viewer restarts from a key
            // frame and is handed some frames again, which is the fallback working, so more than 81 is allowed.
            AssertDecodable(got);
            Assert.True(got.Select(f => f.Id).Distinct().Count() >= 81, "some frame never arrived");
            Assert.True(views[first - 1].Status.Retransmitted > 0, "the first member should have sent some packets again");
            Assert.True(views[second - 1].Status.Recovered > 0);
        }
        finally { foreach (var v in views) v.Dispose(); source.Dispose(); }
    }

    [Fact]
    public async Task After_a_loss_too_long_to_repair_the_viewer_restarts_from_the_key_frame_its_parent_kept()
    {
        var (c, source, views, first, second) = await Chain();
        using var cluster = c;
        try
        {
            var got = Watch(views[second - 1]);
            int asked = 0;
            source.KeyFrameRequested += () => Interlocked.Increment(ref asked);

            await source.SendFrameAsync(Frame(0, 12_000), isKey: true);
            for (int n = 1; n <= 20; n++) { await source.SendFrameAsync(Frame(n, 3000), false); await Task.Delay(20); }
            await TestUtil.WaitFor(() => got.Count == 21, 10_000);

            // Nothing gets through for 300 small frames, sent back to back so the gap is short (a longer one is taken for
            // a dead parent): more than a viewer will ask to have sent again, but little enough that the parent still
            // holds the key frame and everything since.
            bool blocked = true;
            c.Nodes[first].Mux.OutboundFilter = (_, to) => to.Port != c.Nodes[second].LocalPort || !Volatile.Read(ref blocked);
            for (int n = 21; n <= 320; n++) await source.SendFrameAsync(Frame(n, 500), false);
            Volatile.Write(ref blocked, false);
            for (int n = 321; n <= 340; n++) { await source.SendFrameAsync(Frame(n, 500), false); await Task.Delay(30); }

            try { await TestUtil.WaitFor(() => got.Count(f => f.IsKey && f.Id == 1) >= 2 && got.Last().Id >= 335, 20_000); }
            catch (TimeoutException)
            {
                var st = views[second - 1].Status;
                Assert.Fail($"delivered {got.Count}, key id 1 delivered {got.Count(f => f.IsKey && f.Id == 1)} times, last id {got.Last().Id}, " +
                            $"lost {views[second - 1].FramesLost}, asked at sharer {asked}, viewer: parent={st.Parent?.Short} dup={st.Duplicates} delivered={st.Delivered} recovered={st.Recovered}; " +
                            $"first: children={views[first - 1].Status.Children.Count} retransmitted={views[first - 1].Status.Retransmitted}");
            }
            AssertDecodable(got.ToArray());
            Assert.Equal(0, asked);   // the parent's copy of the key frame was enough, the sharer was not troubled
        }
        finally { foreach (var v in views) v.Dispose(); source.Dispose(); }
    }

    [Fact]
    public async Task A_replay_over_a_link_that_keeps_losing_packets_is_repaired_and_still_completes()
    {
        var (c, source, views, first, second) = await Chain();
        using var cluster = c;
        try
        {
            var got = Watch(views[second - 1]);
            await source.SendFrameAsync(Frame(0, 12_000), isKey: true);
            for (int n = 1; n <= 20; n++) { await source.SendFrameAsync(Frame(n, 3000), false); await Task.Delay(20); }
            await TestUtil.WaitFor(() => got.Count == 21, 10_000);

            // A blackout too long to repair, then a link that loses 4% of what it carries for the rest of the test:
            // the replay that restarts the viewer has to get through it as well.
            var random = new Random(11);
            bool blocked = true;
            c.Nodes[first].Mux.OutboundFilter = (_, to) => to.Port != c.Nodes[second].LocalPort || (!Volatile.Read(ref blocked) && random.NextDouble() > 0.04);
            for (int n = 21; n <= 320; n++) await source.SendFrameAsync(Frame(n, 500), false);
            Volatile.Write(ref blocked, false);
            for (int n = 321; n <= 350; n++) { await source.SendFrameAsync(Frame(n, 500), false); await Task.Delay(30); }

            try { await TestUtil.WaitFor(() => got.Count(f => f.IsKey && f.Id == 1) >= 2 && got.Last().Id >= 345, 25_000); }
            catch (TimeoutException)
            {
                var st = views[second - 1].Status;
                Assert.Fail($"delivered {got.Count}, key id 1 delivered {got.Count(f => f.IsKey && f.Id == 1)} times, last id {got.Last().Id}, lost {views[second - 1].FramesLost}, " +
                            $"viewer: parent={st.Parent?.Short} recovered={st.Recovered}; first: retransmitted={views[first - 1].Status.Retransmitted}");
            }
            AssertDecodable(got.ToArray());
        }
        finally { foreach (var v in views) v.Dispose(); source.Dispose(); }
    }

    [Fact]
    public async Task When_nobody_nearby_has_a_key_frame_the_sharer_is_asked_for_one()
    {
        var (c, source, views, first, second) = await Chain();
        using var cluster = c;
        try
        {
            var got = Watch(views[second - 1]);
            int asked = 0;
            int next = 0;
            source.KeyFrameRequested += () =>
            {
                Interlocked.Increment(ref asked);
                _ = source.SendFrameAsync(Frame(1000 + Interlocked.Increment(ref next), 10_000), isKey: true);
            };

            await source.SendFrameAsync(Frame(0, 10_000), isKey: true);
            // Enough frames that the copy anyone keeps (up to half a megabyte) is thrown away.
            for (int n = 1; n <= 90; n++) { await source.SendFrameAsync(Frame(n, 8000), false); await Task.Delay(8); }
            await TestUtil.WaitFor(() => got.Count >= 91, 20_000);

            bool blocked = true;
            c.Nodes[first].Mux.OutboundFilter = (_, to) => to.Port != c.Nodes[second].LocalPort || !Volatile.Read(ref blocked);
            for (int n = 91; n <= 100; n++) { await source.SendFrameAsync(Frame(n, 8000), false); await Task.Delay(30); }
            Volatile.Write(ref blocked, false);
            for (int n = 101; n <= 140; n++) { await source.SendFrameAsync(Frame(n, 8000), false); await Task.Delay(30); }

            await TestUtil.WaitFor(() => asked >= 1 && got.Any(f => f.IsKey && f.Id > 91), 20_000);
            var all = got.ToArray();
            AssertDecodable(all);
        }
        finally { foreach (var v in views) v.Dispose(); source.Dispose(); }
    }

    [Fact]
    public async Task A_stream_too_big_for_the_shared_server_is_not_carried_over_it_but_a_small_one_is()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 2, rootFanout: 2);
        // The fourth member can only reach the others through the shared server.
        c.Nodes[3].OnSharedServerOverride = _ => true;
        using var video = c.Root.StartMedia(c.Rooms[0], "screen", "video", kbps: 2000);
        using var audio = c.Root.StartMedia(c.Rooms[0], "sound", "sound", kbps: 64, upstream: false);
        await TestUtil.WaitFor(() => c.Rooms.Skip(1).All(r => r.MediaGroups.Count == 2), 20_000);

        var viewers = new List<MediaView>();
        try
        {
            for (int i = 1; i <= 3; i++) viewers.Add(c.Nodes[i].WatchMedia(c.Rooms[i], video.Info.Id));
            await TestUtil.WaitFor(() => viewers[0].Status.Attached && viewers[1].Status.Attached, 20_000);
            await TestUtil.WaitFor(() => viewers[2].Status.OverServer, 20_000);
            Assert.False(viewers[2].Status.Attached);

            var small = c.Nodes[3].WatchMedia(c.Rooms[3], audio.Info.Id);
            viewers.Add(small);
            await TestUtil.WaitFor(() => small.Status.Attached, 20_000);
            Assert.False(small.Status.OverServer);
        }
        finally { foreach (var v in viewers) v.Dispose(); }
    }

    [Fact]
    public async Task A_member_whose_own_connection_runs_through_the_server_does_not_take_a_big_stream_on_as_a_child()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(3, fanout: 2, rootFanout: 2);
        c.Root.OnSharedServerOverride = _ => true;   // everything the sharer sends goes through the server
        using var video = c.Root.StartMedia(c.Rooms[0], "screen", "video", kbps: 2000);
        await TestUtil.WaitFor(() => c.Rooms.Skip(1).All(r => r.MediaGroups.Count == 1), 20_000);
        var views = new List<MediaView> { c.Nodes[1].WatchMedia(c.Rooms[1], video.Info.Id) };
        try
        {
            await Task.Delay(3000);
            Assert.False(views[0].Status.Attached);
            Assert.Empty(video.Status.Children);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }
}
