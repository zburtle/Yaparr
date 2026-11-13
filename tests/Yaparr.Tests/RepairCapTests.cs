using Yaparr.Chat;

namespace Yaparr.Tests;

/// <summary>
/// Repairs may use a quarter of the stream (ChatNode.RepairShare). They used to be paced at the stream's own rate, so a sender facing loss could double what it put on a
/// link that was already full; a viewer on a Wi-Fi link that carried about 6 Mbit/s lost half its packets to a 6 Mbit/s share with the repairs on top, and 99% fewer
/// frames at half the rate.
/// </summary>
public class RepairCapTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(6000, 187_500)]
    [InlineData(3000, 93_750)]
    [InlineData(1000, 32_000)]   // a floor: a thin stream can still be repaired
    [InlineData(64, 32_000)]
    public void Repairs_may_use_a_quarter_of_the_stream_and_not_less_than_a_floor(int kbps, double bytesPerSecond)
        => Assert.Equal(bytesPerSecond, ChatNode.RepairBytesPerSecond(kbps));

    private async Task<MediaStatus> RunAsync(int kbps, int frameBytes, int frames, int gapMs, double loss, int seed)
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: kbps);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            views[0].FrameReceived += _ => { };
            var drop = new Random(seed);
            int viewerPort = c.Nodes[1].LocalPort;
            // the big packets only (the small ones carry the requests and keep the connection alive), the repairs among them
            c.Root.Mux.OutboundFilter = (p, to) => to.Port != viewerPort || p.Length < 600 || drop.NextDouble() > loss;
            var frame = Enumerable.Range(0, frameBytes).Select(i => (byte)(i * 7)).ToArray();
            await source.SendFrameAsync(frame, isKey: true);
            for (int n = 1; n <= frames; n++) { await source.SendFrameAsync(frame, isKey: false); await Task.Delay(gapMs); }
            await Task.Delay(3000);
            var server = source.Status;
            var viewer = views[0].Status;
            output.WriteLine($"loss {loss:P0}: sender resent {server.Retransmitted}, held back {server.RepairsRefused}; viewer: gaps {viewer.GapsNoticed}, repaired after asking {viewer.RepairedAfterAsking}, never {viewer.GaveUp}, asked for {viewer.PacketsAsked}");
            return server with { PacketsSeen = viewer.PacketsSeen, GapsNoticed = viewer.GapsNoticed, RepairedAfterAsking = viewer.RepairedAfterAsking, GaveUp = viewer.GaveUp, PacketsAsked = viewer.PacketsAsked };
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task On_a_link_that_loses_a_good_part_the_repairs_stay_within_their_share_and_the_rest_are_held_back()
    {
        // 3 Mbit/s for about five seconds over a link that loses 35%: the viewer asks for far more than a quarter of the stream.
        var s = await RunAsync(kbps: 3000, frameBytes: 11_000, frames: 150, gapMs: 30, loss: 0.35, seed: 3);
        Assert.True(s.RepairsRefused > 100, $"{s.RepairsRefused} repairs held back; the viewer asked for {s.PacketsAsked}");
        // a quarter of 3 Mbit/s is 94 KB/s: about 85 packets a second for about eight seconds (the stream and the wait after it), and the bucket's burst
        Assert.True(s.Retransmitted <= 85 * 9 + 30, $"{s.Retransmitted} packets sent again");
    }

    [Fact]
    public async Task On_a_link_that_loses_a_little_the_cap_does_not_get_in_the_way_of_repairs()
    {
        // 8 Mbit/s over a link that loses 3%: repairs need about 3% of the stream, well inside the quarter.
        var s = await RunAsync(kbps: 8000, frameBytes: 30_000, frames: 100, gapMs: 30, loss: 0.03, seed: 9);
        Assert.True(s.GapsNoticed > 50, $"{s.GapsNoticed} gaps");
        Assert.True(s.RepairedAfterAsking >= s.GapsNoticed * 0.85, $"{s.RepairedAfterAsking} of {s.GapsNoticed} gaps repaired");
        Assert.True(s.RepairsRefused <= s.GapsNoticed * 0.1, $"{s.RepairsRefused} repairs held back");
    }

    [Fact]
    public async Task The_share_follows_the_bitrate_the_stream_carries_now()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        Assert.Equal(6000, source.EffectiveKbps);   // announced
        source.EffectiveKbps = 2700;
        Assert.Equal(2700, source.EffectiveKbps);
        Assert.Equal(0, source.Status.RepairsRefused);
    }
}
