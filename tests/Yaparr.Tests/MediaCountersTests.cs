using Yaparr.Chat;
using Yaparr.Desktop;

namespace Yaparr.Tests;

public class MediaCountersTests
{
    private static byte[] Frame(int n, int size) => Enumerable.Range(0, size).Select(i => (byte)(n * 31 + i)).ToArray();

    [Fact]
    public async Task A_clean_link_counts_the_packets_that_came_and_nothing_else()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            views[0].FrameReceived += _ => { };
            await source.SendFrameAsync(Frame(0, 20_000), isKey: true);
            for (int n = 1; n <= 30; n++) { await source.SendFrameAsync(Frame(n, 3000), isKey: false); await Task.Delay(20); }
            await TestUtil.WaitFor(() => views[0].FramesDelivered >= 31, 15_000);

            var s = views[0].Status;
            Assert.True(s.PacketsSeen >= 31, $"{s.PacketsSeen} packets came");
            Assert.Equal((0, 0, 0, 0, 0, 0, 0), (s.GapsNoticed, s.BigGaps, s.ArrivedLate, s.RepairedAfterAsking, s.GaveUp, s.NacksSent, s.PacketsAsked));
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task Lost_packets_are_counted_as_gaps_asked_for_and_repaired_and_the_ones_never_seen_are_counted_too()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            views[0].FrameReceived += _ => { };
            var drop = new Random(11);
            int viewerPort = c.Nodes[1].LocalPort;
            c.Root.Mux.OutboundFilter = (_, to) => to.Port != viewerPort || drop.NextDouble() > 0.06;
            await source.SendFrameAsync(Frame(0, 20_000), isKey: true);
            for (int n = 1; n <= 80; n++) { await source.SendFrameAsync(Frame(n, 4000), isKey: false); await Task.Delay(25); }
            await TestUtil.WaitFor(() => views[0].Status.RepairedAfterAsking > 0 && views[0].Status.PacketsAsked > 0, 15_000);
            await Task.Delay(2000);   // what is not repaired is given up on after a second and a half

            var s = views[0].Status;
            Assert.True(s.PacketsSeen > 100, $"{s.PacketsSeen} packets came");
            Assert.True(s.GapsNoticed > 0, "gaps should have been noticed");
            Assert.True(s.NacksSent > 0 && s.PacketsAsked >= s.NacksSent, $"{s.NacksSent} requests for {s.PacketsAsked} packets");
            Assert.True(s.RepairedAfterAsking > 0, "some packets should have come after being asked for");
            // Every gap ends one way: it turned up by itself, it was repaired after being asked for, or it was given up on.
            Assert.True(s.ArrivedLate + s.RepairedAfterAsking + s.GaveUp <= s.GapsNoticed, "no gap can end twice");
            Assert.Equal(s.Recovered, s.ArrivedLate + s.RepairedAfterAsking);   // the old figure is the two together
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public void The_packet_line_of_a_watch_says_what_became_of_the_packets()
    {
        var figures = new WatchFigures("x", 0, 100, 100, 1000, 3, 12, 0, 0, 0, true, null,
            PacketsSeen: 5000, GapsNoticed: 40, BigGaps: 1, ArrivedLate: 9, RepairedAfterAsking: 20, NacksSent: 15, PacketsAsked: 31, GaveUp: 11);
        string line = figures.DescribePackets();
        Assert.Equal("5000 packets came, 40 were missing when a later one arrived (9 turned up by themselves, 20 after being asked for, 11 never), 15 resend requests for 31 packets, 1 gaps too big to ask about, 0 copies refused (a packet already taken, or one from too far back)", line);
        Assert.Contains(line, figures.Describe(10_000));
    }
}
