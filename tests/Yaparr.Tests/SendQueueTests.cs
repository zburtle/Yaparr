using System.Collections.Concurrent;
using System.Diagnostics;
using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>
/// Sending has a thread of its own: the encoding loop hands each frame to a queue and goes on, so a big frame spread out at the paced rate no longer makes the
/// share's clock miss its ticks (1.77.0 sent 17 of 30 pictures a second while its encoder kept up). What the queue does is counted, and when sending falls more
/// than a second behind what is queued is thrown away and a key frame asked for.
/// </summary>
public class SendQueueTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const long Ms = TimeSpan.TicksPerMillisecond;

    [Fact]
    public void The_sending_part_says_how_long_frames_took_to_go_out_and_how_long_they_waited()
    {
        var stats = new ShareStats();
        stats.Sent(sendTicks: 10 * Ms, queuedTicks: 0, frameTicks: 33 * Ms);
        stats.Sent(sendTicks: 170 * Ms, queuedTicks: 40 * Ms, frameTicks: 33 * Ms);
        stats.Encoded(9 * Ms, overBudget: false);
        stats.DroppedFromQueue(3);
        var figures = stats.Figures(new KeyFramePacer(), 0);
        string line = figures.DescribeEncoding(33.3);
        output.WriteLine(line);
        Assert.Contains("sending 90.0 ms per frame on average and 170.0 ms at most", line);
        Assert.Contains("waiting to be sent 20.0 ms on average and 40.0 ms at most (1 of 2 waited longer than the 33.3 ms between pictures)", line);
        Assert.Contains("3 frames thrown away when sending fell more than a second behind", line);
        // a minute is the difference of two totals
        var later = new ShareStats();
        Assert.Equal(0, later.Figures(new KeyFramePacer(), 0).Since(later.Figures(new KeyFramePacer(), 0)).Sends);
        var quiet = new ShareStats();
        quiet.Encoded(9 * Ms, overBudget: false);
        quiet.Sent(10 * Ms, 0, 33 * Ms);
        Assert.DoesNotContain("thrown away", quiet.Figures(new KeyFramePacer(), 0).DescribeEncoding(33.3));   // said only when it happened
        // the largest values per reader
        var maxima = stats.TakeMaxima(0);
        Assert.Equal(170 * Ms, maxima.SendTicks);
        Assert.Equal(40 * Ms, maxima.QueueTicks);
        Assert.Equal(0, stats.TakeMaxima(0).SendTicks);
    }

    [Fact]
    public async Task Handing_frames_to_the_sender_never_waits_and_they_all_go_out_in_order()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 4000);
        var views = await c.WatchAllAsync(source.Info.Id);
        var got = new ConcurrentQueue<byte>();
        views[0].FrameReceived += f => got.Enqueue(f.Data.Span[0]);
        using var sharer = new ScreenSharer(source, new ScreenOptions { Fps = 30, Kbps = 4000, X11Display = null, AdaptQuality = false });
        sharer.StartSending();

        // Twelve frames of 40 KB: at three times 4 Mbit/s each takes about 25 ms to go out, more than the 33 ms between pictures for the first ones together.
        var clock = Stopwatch.StartNew();
        for (int n = 0; n < 12; n++)
        {
            var frame = new byte[40 * 1024];
            frame[0] = (byte)n;
            sharer.Enqueue(frame, isKey: n == 0);
        }
        var handOver = clock.Elapsed;
        output.WriteLine($"handed over in {handOver.TotalMilliseconds:0.0} ms");
        Assert.True(handOver < TimeSpan.FromMilliseconds(50), $"handing over took {handOver.TotalMilliseconds:0} ms: the encoder would have waited");

        await TestUtil.WaitFor(() => sharer.Frames == 12 && got.Count == 12, 15_000);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), got);   // in the order they were made
        var figures = sharer.Figures();
        output.WriteLine(figures.DescribeSending(sharer.FrameBudgetMs));
        Assert.Equal(12, figures.Sends);
        Assert.True(figures.SendTicks > 0);
        Assert.True(figures.QueueMaxTicksEver > 0, "the later frames waited behind the first ones");
        Assert.Equal(0, figures.Dropped);
        Assert.Equal(0, sharer.QueuedFrames);
    }

    [Fact]
    public async Task When_sending_falls_more_than_a_second_behind_the_queue_is_emptied_and_a_key_frame_is_asked_for()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 300);
        await c.WatchAllAsync(source.Info.Id);
        using var sharer = new ScreenSharer(source, new ScreenOptions { Fps = 30, Kbps = 300, X11Display = null, AdaptQuality = false });
        sharer.StartSending();

        // Far more than the paced rate can carry (about 110 KB a second at three times 300 kbit/s): seconds of frames.
        sharer.Enqueue(new byte[60 * 1024], isKey: true);
        for (int n = 0; n < 20; n++) sharer.Enqueue(new byte[60 * 1024], isKey: false);
        long requestedBefore = sharer.Figures().KeyRequests;
        await Task.Delay(MaxBacklogPlus);

        sharer.Enqueue(new byte[1000], isKey: false);   // finds the head of the queue more than a second old
        var figures = sharer.Figures();
        output.WriteLine(figures.DescribeSending(sharer.FrameBudgetMs));
        Assert.True(figures.Dropped > 0, "what was queued is thrown away");
        Assert.Equal(requestedBefore + 1, figures.KeyRequests);   // and a key frame is asked for
        Assert.Equal(0, sharer.QueuedFrames);                     // the new frame too: it cannot be decoded without the ones thrown away

        long dropped = figures.Dropped;
        sharer.Enqueue(new byte[1000], isKey: false);              // still waiting for the key frame
        Assert.Equal(dropped + 1, sharer.Figures().Dropped);
        long framesNow = sharer.Frames;
        sharer.Enqueue(new byte[1000], isKey: true);               // here it is
        Assert.Equal(dropped + 1, sharer.Figures().Dropped);       // not thrown away
        await TestUtil.WaitFor(() => sharer.Frames > framesNow, 15_000);   // it goes out
    }

    private static readonly TimeSpan MaxBacklogPlus = ScreenSharer.MaxSendBacklog + TimeSpan.FromMilliseconds(200);
}
