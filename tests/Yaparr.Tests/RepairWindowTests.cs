using Yaparr.Chat;

namespace Yaparr.Tests;

/// <summary>
/// A repair is a packet that was lost and is sent again after the viewer asked for it. It arrives well after the packets that came in its place: the viewer
/// notices the gap, waits a moment before asking, and the answer takes the time of the round trip. At a few Mbit/s hundreds of packets go by in that time,
/// and the receiver must still take the repair; a receiver that remembered only the last 64 packet numbers threw most repairs away as duplicates (found in the
/// first real logs of a Wi-Fi viewer: the sharer sent about 6,000 packets again a minute and the viewer took about 250).
/// </summary>
public class RepairWindowTests
{
    [Fact]
    public void A_packet_is_taken_once_in_order_and_a_second_copy_is_refused()
    {
        var window = new SeqWindow();
        for (uint seq = 100; seq < 200; seq++) Assert.True(window.Accept(seq), $"{seq} the first time");
        for (uint seq = 100; seq < 200; seq++) Assert.False(window.Accept(seq), $"{seq} the second time");
    }

    [Fact]
    public void A_packet_that_was_lost_is_taken_when_it_comes_late_and_only_once_even_hundreds_of_packets_later()
    {
        var window = new SeqWindow();
        for (uint seq = 0; seq < 1000; seq++) if (seq != 500) Assert.True(window.Accept(seq));   // 500 was lost; 499 packets have gone by since
        Assert.True(window.Accept(500), "the repair of a packet 499 numbers back");
        Assert.False(window.Accept(500), "and a second copy of the repair");
        // the packet before it that did arrive is still known
        Assert.False(window.Accept(499));
    }

    [Fact]
    public void A_repair_is_taken_up_to_the_size_of_the_window_back_and_not_beyond_it()
    {
        var window = new SeqWindow();
        Assert.True(window.Accept(10_000));
        // everything from 10,000 back to a window's length behind it is unseen and may still turn up
        Assert.True(window.Accept(10_000 - 1));
        Assert.True(window.Accept(10_000 - (uint)(SeqWindow.Size - 1)));
        Assert.False(window.Accept(10_000 - (uint)SeqWindow.Size), "a window's length behind is too old");
        Assert.False(window.Accept(1), "and a stream's start is far too old");
    }

    [Fact]
    public void A_jump_ahead_forgets_what_the_window_held_and_a_jump_of_more_than_a_window_clears_it()
    {
        var window = new SeqWindow();
        for (uint seq = 0; seq < 100; seq++) window.Accept(seq);
        Assert.True(window.Accept(100 + 3000));       // the packets in between are unseen, and 0..99 are still inside the window
        Assert.False(window.Accept(50), "seen before the jump");
        Assert.True(window.Accept(2000), "unseen, inside the window");
        Assert.True(window.Accept(100 + 3000 + (uint)SeqWindow.Size + 5));   // a jump of more than a window
        Assert.False(window.Accept(100 + 3000), "now too old");
        Assert.True(window.Accept(100 + 3000 + (uint)SeqWindow.Size + 4), "just inside, unseen");
    }

    [Fact]
    public void The_numbers_wrap_around_without_notice()
    {
        var window = new SeqWindow();
        uint start = uint.MaxValue - 1000;
        for (uint i = 0; i < 2000; i++) if (i != 1500) Assert.True(window.Accept(unchecked(start + i)));   // runs through zero; one is lost
        Assert.True(window.Accept(unchecked(start + 1500)), "the lost one, after the numbers wrapped");
        Assert.False(window.Accept(unchecked(start + 1500)));
        Assert.False(window.Accept(unchecked(start + 10)), "a copy from before the wrap");
    }

    /// <summary>
    /// The whole path: a stream of 8 Mbit/s over a link that loses one packet in twenty (the repairs too), to a viewer that asks for what is missing. Hundreds
    /// of packets go by while a repair is on its way, so every repair arrives after 64 packets, which a receiver with a window of 64 refuses: it repaired a
    /// handful of what it asked for. With the window it repairs most.
    /// </summary>
    [Fact]
    public async Task At_a_high_packet_rate_the_repairs_that_arrive_late_are_taken_and_most_of_what_was_lost_is_got_back()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 8000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            views[0].FrameReceived += _ => { };
            var drop = new Random(21);
            int viewerPort = c.Nodes[1].LocalPort;
            // the big packets only (the small ones carry the requests and keep the connection alive), the repairs among them
            c.Root.Mux.OutboundFilter = (p, to) => to.Port != viewerPort || p.Length < 600 || drop.NextDouble() > 0.05;
            var frame = Enumerable.Range(0, 40_000).Select(i => (byte)(i * 7)).ToArray();
            await source.SendFrameAsync(frame, isKey: true);
            for (int n = 1; n <= 120; n++) { await source.SendFrameAsync(frame, isKey: false); await Task.Delay(8); }
            await Task.Delay(3000);   // what is not repaired by now is given up on

            var s = views[0].Status;
            output(s);
            Assert.True(s.GapsNoticed > 150, $"{s.GapsNoticed} gaps; the link should have lost plenty");
            Assert.True(s.RepairedAfterAsking >= s.GapsNoticed * 0.6, $"{s.RepairedAfterAsking} of {s.GapsNoticed} gaps were repaired after asking, {s.GaveUp} never, {s.Duplicates} copies thrown away as duplicates");
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    private static void output(MediaStatus s) => Console.WriteLine($"gaps {s.GapsNoticed}, repaired after asking {s.RepairedAfterAsking}, turned up by themselves {s.ArrivedLate}, never {s.GaveUp}, duplicates {s.Duplicates}");
}
