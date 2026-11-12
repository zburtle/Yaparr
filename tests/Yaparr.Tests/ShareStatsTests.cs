using Yaparr.Desktop;
using Yaparr.Screen;

namespace Yaparr.Tests;

public class ShareStatsTests
{
    private static readonly long Ms = TimeSpan.TicksPerMillisecond;

    [Fact]
    public void Key_frames_and_the_others_are_counted_and_measured_apart()
    {
        var stats = new ShareStats();
        stats.Frame(200 * 1024, key: true);
        stats.Frame(10 * 1024, key: false);
        stats.Frame(20 * 1024, key: false);
        stats.Frame(100 * 1024, key: true);

        var f = stats.Figures(new KeyFramePacer(), restarts: 0);
        Assert.Equal((2, 2), (f.Keys, f.Deltas));
        Assert.Equal(150, f.KeyAverageKb, 1);
        Assert.Equal(15, f.DeltaAverageKb, 1);
        Assert.Equal(200 * 1024, f.KeyMaxEver);
        Assert.Equal(20 * 1024, f.DeltaMaxEver);
    }

    [Fact]
    public void Each_reader_gets_the_largest_values_since_it_last_asked_without_taking_them_from_the_other()
    {
        var stats = new ShareStats();
        stats.Encoded(5 * Ms, overBudget: false);
        stats.Encoded(9 * Ms, overBudget: false);
        Assert.Equal(9, stats.TakeMaxima(0).EncodeMs, 3);
        stats.Encoded(3 * Ms, overBudget: false);
        Assert.Equal(3, stats.TakeMaxima(0).EncodeMs, 3);    // the first reader starts afresh
        Assert.Equal(9, stats.TakeMaxima(1).EncodeMs, 3);    // the second still has the nine
        Assert.Equal(0, stats.TakeMaxima(1).EncodeMs, 3);
        Assert.Equal(9, stats.Figures(new KeyFramePacer(), 0).EncodeMaxTicksEver / (double)Ms, 3);
    }

    [Fact]
    public void A_period_is_the_difference_of_two_sets_of_totals()
    {
        var stats = new ShareStats();
        var pacer = new KeyFramePacer();
        stats.Frame(1000, key: true);
        stats.Encoded(4 * Ms, overBudget: false);
        var before = stats.Figures(pacer, 0);

        stats.Frame(1000, key: false);
        stats.Frame(3000, key: false);
        stats.Encoded(6 * Ms, overBudget: true);
        stats.Encoded(8 * Ms, overBudget: false);
        stats.Waited(2 * Ms, 16 * Ms);
        var minute = stats.Figures(pacer, 2).Since(before);

        Assert.Equal((0, 2), (minute.Keys, minute.Deltas));
        Assert.Equal(2, minute.Encodes);
        Assert.Equal(7, minute.EncodeAverageMs, 3);
        Assert.Equal(1, minute.OverBudget);
        Assert.Equal(2, minute.WaitAverageMs, 3);
    }

    [Fact]
    public void Pictures_that_came_in_a_burst_and_waits_of_more_than_two_pictures_are_counted()
    {
        var stats = new ShareStats();
        long frame = 16 * Ms;
        stats.Encoded(Ms, overBudget: false);   // the line is only written when something was encoded
        foreach (var wait in new[] { 16 * Ms, 100 * Ms, Ms, 2 * Ms, 3 * Ms, 17 * Ms, 32 * Ms, 40 * Ms })
            stats.Waited(wait, frame);
        var figures = stats.Figures(new KeyFramePacer(), 0);
        Assert.Equal(8, figures.Reads);
        Assert.Equal(3, figures.Bunched);    // 1, 2 and 3 ms: under a quarter of 16 ms
        Assert.Equal(2, figures.Stalled);    // 100 and 40 ms: over two pictures (32 ms is not)
        Assert.Contains("2 waits of more than two pictures and 3 pictures that came in a burst", figures.DescribeEncoding(16) + "");
    }

    [Fact]
    public void The_pacer_counts_requests_and_says_how_long_one_waited_for_its_key_frame()
    {
        var pacer = new KeyFramePacer(500);
        pacer.Request(10_000);
        Assert.True(pacer.ShouldForce(10_020));
        Assert.Equal(180, pacer.KeySent(10_180));       // asked at 10 000, answered at 10 180
        Assert.Equal((1, 1, 0, 0), (pacer.Requested, pacer.Forced, pacer.Deferred, pacer.Satisfied));
        Assert.Equal(-1, pacer.KeySent(11_000));         // a regular key frame with nobody waiting
    }

    [Fact]
    public void A_request_that_has_to_wait_is_counted_once_and_a_regular_key_frame_that_meets_one_is_counted_as_having_done_so()
    {
        var pacer = new KeyFramePacer(500);
        pacer.KeySent(20_000);
        pacer.Request(20_100);
        Assert.False(pacer.ShouldForce(20_200));
        Assert.False(pacer.ShouldForce(20_300));         // still the same request
        Assert.Equal(200, pacer.KeySent(20_300));        // the regular key frame came first
        Assert.Equal((1, 0, 1, 1), (pacer.Requested, pacer.Forced, pacer.Deferred, pacer.Satisfied));
    }

    [Fact]
    public void The_key_frame_line_says_what_became_of_the_requests()
    {
        var stats = new ShareStats();
        var pacer = new KeyFramePacer(500);
        pacer.Request(1000);
        pacer.ShouldForce(1010);
        stats.Frame(120 * 1024, key: true);
        stats.Answered(pacer.KeySent(1060));
        string line = stats.Figures(pacer, 0).DescribeKeys();

        Assert.Contains("1 key frames (1 made because a viewer asked)", line);
        Assert.Contains("key frame 120 KB on average", line);
        Assert.Contains("requests from viewers 1: 1 answered with a new key frame (after 60 ms on average, 60 ms at most)", line);
    }

    [Fact]
    public void The_encoding_line_is_empty_when_nothing_was_encoded_in_this_program_and_counts_the_slow_pictures_when_something_was()
    {
        var stats = new ShareStats();
        Assert.Equal("", stats.Figures(new KeyFramePacer(), 0).DescribeEncoding(16.7));
        stats.Encoded(20 * Ms, overBudget: true);
        stats.Encoded(4 * Ms, overBudget: false);
        stats.Waited(10 * Ms, 16 * Ms);
        string line = stats.Figures(new KeyFramePacer(), 0).DescribeEncoding(16.7);
        Assert.Contains("12.0 ms per picture on average and 20.0 ms at most (1 of 2 over the 16.7 ms there is for each)", line);
        Assert.Contains("waiting for the capture 10.0 ms on average", line);
    }

    [Fact]
    public void The_graphics_card_tools_line_becomes_words_and_unknown_figures_are_left_out()
    {
        string line = NvidiaSmi.Describe("51, 7, 12, 1486, 49, 382, 17.02, 1, 60, 3500\r\n")!;
        Assert.Contains("card 51% busy", line);
        Assert.Contains("video encoder 7% busy", line);
        Assert.Contains("1486 MB of video memory used", line);
        Assert.Contains("1 encoder sessions, averaging 60 fps and 3500 µs a picture", line);

        string partial = NvidiaSmi.Describe("51, [N/A], [N/A], 1486, 49, 382, [N/A], [N/A], [N/A], [N/A]")!;
        Assert.Contains("card 51% busy", partial);
        Assert.DoesNotContain("encoder", partial);
        Assert.Null(NvidiaSmi.Describe(""));
        Assert.Null(NvidiaSmi.Describe("not a figure line"));
    }

    [Fact]
    public void The_line_for_a_minute_of_sharing_has_every_figure_a_test_run_is_judged_by()
    {
        var stats = new ShareStats();
        var pacer = new KeyFramePacer(500);
        pacer.Request(1000);
        pacer.ShouldForce(1010);
        stats.Frame(150 * 1024, key: true);
        stats.Answered(pacer.KeySent(1090));
        for (int i = 0; i < 59; i++) { stats.Frame(9 * 1024, key: false); stats.Encoded(5 * Ms, overBudget: false); stats.Waited(11 * Ms, 16 * Ms); }
        var interval = stats.Figures(pacer, restarts: 1).Since(new ShareFigures());
        var minute = new SharingMinute(59.8, 60, 5200, "H.264 on the NVIDIA graphics card", InProcess: true, Picture: (1920, 1080), interval, stats.TakeMaxima(1), 16.7,
            LongestGapMs: 41, Resent: 3, Dropped: 0, Watching: 2, DirectPaths: new[] { "ab12 direct" }, AppPercent: 38, CapturePercent: 22, MemoryMb: 410,
            Collections: (12, 3, 1), PausedMs: 4.5, Card: "card 40% busy, video encoder 12% busy", Bad: false);

        string line = minute.Describe();
        foreach (var part in new[]
        {
            "call quality: sharing: 59.8 of 60 fps, 5200 kbit/s, H.264 on the NVIDIA graphics card, encoded in this program, picture 1920x1080",
            "1 key frames (1 made because a viewer asked)", "key frame 150 KB on average", "requests from viewers 1: 1 answered with a new key frame (after 90 ms on average",
            "encoding 5.0 ms per picture on average", "waiting for the capture 11.0 ms on average", "longest wait for a frame 41 ms",
            "3 packets sent again, 0 dropped, 2 watching, 1 of them directly (ab12 direct)", "this program 38% of a core, capture FFmpeg 22%", "memory 410 MB",
            "garbage collections 12/3/1 (paused 5 ms)", "capture restarted 1x", "graphics card: card 40% busy, video encoder 12% busy",
        })
            Assert.Contains(part, line);
        Assert.DoesNotContain("(a problem)", line);
        Assert.Contains("(a problem)", (minute with { Bad = true }).Describe());
        Assert.Contains("encoded by a separate FFmpeg", (minute with { InProcess = false }).Describe());
    }

    [Fact]
    public void The_card_and_driver_are_named_for_the_log()
    {
        Assert.Equal("NVIDIA GeForce RTX 3060, driver 616.56, 12288 MB of video memory, PCIe generation 4 x16",
            NvidiaSmi.DescribeInfo("NVIDIA GeForce RTX 3060, 616.56, 12288, 4, 16\r\n"));
        Assert.Equal("NVIDIA GeForce RTX 3060, driver 616.56", NvidiaSmi.DescribeInfo("NVIDIA GeForce RTX 3060, 616.56, [N/A], [N/A], [N/A]"));
        Assert.Null(NvidiaSmi.DescribeInfo(""));
    }
}
