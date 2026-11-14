using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>Adaptive quality acting on the bitrate steps: the policy's decision reaches the encoder (as a pending bitrate for the thread that encodes) and the stream's repair share.</summary>
public class ActingQualityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static ViewerQualitySample Bad => new(true, true, 12, true);
    private static ViewerQualitySample Good => new(true, false, 60, false);

    private static ScreenOptions Options(bool adapt = true, bool inProcess = true)
        => new() { Fps = 60, Kbps = 6000, MaxWidth = 1920, MaxHeight = 1080, X11Display = null, AdaptQuality = adapt && inProcess };

    [Fact]
    public async Task When_most_of_the_room_is_in_trouble_the_bitrate_is_lowered_and_repairs_follow_it()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        var views = await c.WatchAllAsync(source.Info.Id);
        using var sharer = new ScreenSharer(source, Options());
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sharer.QualityDecided += said.Enqueue;
        try
        {
            views[0].QualityProvider = () => Bad;
            views[1].QualityProvider = () => Bad;
            views[2].QualityProvider = () => Good;
            sharer.StartQualityWatchForTest();
            Assert.Equal(6000, source.EffectiveKbps);
            await TestUtil.WaitFor(() => !said.IsEmpty, 60_000);
            output.WriteLine(said.First());
            Assert.StartsWith("adaptive quality: lowered to 1080p30 at 4.0 Mbit/s (step 1 of 3)", said.First());
            Assert.DoesNotContain("nothing is changed", said.First());
            Assert.True(sharer.ActingOnQuality);
            Assert.Equal(1, sharer.AppliedStep);
            Assert.Equal(4000, sharer.PendingKbpsForTest);   // waiting for the thread that encodes
            Assert.Equal(4000, source.EffectiveKbps);
            Assert.StartsWith("Quality lowered to 1080p30 at 4.0 Mbit/s: 2 of 3 viewers lost frames for 10 s", sharer.AdaptiveLine);
            Assert.Contains("acting on the presets", sharer.QualityNote);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Theory]
    [InlineData(false, true, true)]    // the setting is off
    [InlineData(true, false, true)]    // a separate FFmpeg encodes: nothing here can change its bitrate
    [InlineData(true, true, false)]    // the encoder here is not running (yet)
    public async Task Without_the_setting_or_an_encoder_here_it_is_still_only_written_down(bool adapt, bool inProcess, bool live)
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        var views = await c.WatchAllAsync(source.Info.Id);
        using var sharer = new ScreenSharer(source, Options(adapt, inProcess));
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sharer.QualityDecided += said.Enqueue;
        try
        {
            views[0].QualityProvider = () => Bad;
            views[1].QualityProvider = () => Bad;
            views[2].QualityProvider = () => Good;
            sharer.StartQualityWatchForTest();
            sharer.SetEncoderLiveForTest(live);
            await TestUtil.WaitFor(() => !said.IsEmpty, 60_000);
            Assert.Contains("nothing is changed", said.First());
            Assert.False(sharer.ActingOnQuality);
            Assert.Equal(0, sharer.AppliedStep);
            Assert.Equal(0, sharer.PendingKbpsForTest);
            Assert.Equal(6000, source.EffectiveKbps);
            Assert.Null(sharer.AdaptiveLine);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task A_level_with_another_frame_rate_restarts_the_capture_and_one_with_the_same_only_changes_the_bitrate()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        var views = await c.WatchAllAsync(source.Info.Id);
        using var sharer = new ScreenSharer(source, Options());
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sharer.QualityDecided += said.Enqueue;
        try
        {
            views[0].QualityProvider = () => Bad;
            views[1].QualityProvider = () => Bad;
            views[2].QualityProvider = () => Good;
            sharer.SetRestartableForTest(true);   // as when the capture runs in this program, at 1080p60
            sharer.StartQualityWatchForTest();
            await TestUtil.WaitFor(() => !said.IsEmpty, 60_000);
            Assert.True(sharer.RestartPendingForTest);              // 1080p60 to 1080p30: another frame rate
            Assert.Equal(0, sharer.PendingKbpsForTest);             // the new run starts at the level's bitrate
            Assert.Equal(1, sharer.AppliedStep);
            var run = sharer.RunOptions();
            Assert.Equal((4000, 30, 1920, 1080), (run.Kbps, run.Fps, run.MaxWidth, run.MaxHeight));
            sharer.SetAppliedStepForTest(2);
            run = sharer.RunOptions();
            Assert.Equal((2800, 30, 1920, 1080), (run.Kbps, run.Fps, run.MaxWidth, run.MaxHeight));   // the same picture at 70%
            sharer.SetAppliedStepForTest(3);
            run = sharer.RunOptions();
            Assert.Equal((2000, 30, 1280, 720), (run.Kbps, run.Fps, run.MaxWidth, run.MaxHeight));
            sharer.SetAppliedStepForTest(0);
            Assert.Equal(60, sharer.RunOptions().Fps);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task A_sender_that_sends_fewer_pictures_than_it_should_does_not_lower_the_quality_for_the_viewers_it_says_so_once()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        var views = await c.WatchAllAsync(source.Info.Id);
        using var sharer = new ScreenSharer(source, Options());
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sharer.QualityDecided += said.Enqueue;
        using var cts = new CancellationTokenSource();
        try
        {
            views[0].QualityProvider = () => Bad;
            views[1].QualityProvider = () => Bad;
            views[2].QualityProvider = () => Good;
            sharer.SetRunStartedForTest(Environment.TickCount64 - 30_000);
            sharer.StartQualityWatchForTest();
            // 40 of the 60 pictures a second, for as long as the test runs; the encoder is never over its time.
            _ = Task.Run(async () => { while (!cts.IsCancellationRequested) { sharer.AddFramesForTest(4); await Task.Delay(100); } });
            await TestUtil.WaitFor(() => !said.IsEmpty, 60_000);
            await Task.Delay(14_000);   // longer than the ten seconds that it takes to act
            Assert.Single(said);
            Assert.Matches(@"is sending \d+ of 60 pictures a second", said.First());
            Assert.Contains("of 60 pictures a second although the encoder keeps up", said.First());
            Assert.Equal(0, sharer.AppliedStep);
            Assert.Equal(0, sharer.PendingKbpsForTest);
            Assert.InRange(sharer.SenderRate, 0.6, 0.75);
            Assert.Contains("so the quality is held", sharer.QualityNote);
        }
        finally { cts.Cancel(); foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task The_frame_rate_a_share_was_meant_to_send_is_averaged_over_the_time_it_ran_at_each_level()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        using var sharer = new ScreenSharer(source, Options());
        sharer.SetRunFpsForTest(60);
        sharer.QualityTick();
        await Task.Delay(1100);
        sharer.QualityTick();
        sharer.SetRunFpsForTest(30);
        await Task.Delay(1100);
        sharer.QualityTick();
        var (fpsSeconds, seconds) = sharer.WantedFpsTotals;
        Assert.InRange(seconds, 2.0, 2.6);
        Assert.InRange(fpsSeconds / seconds, 42, 48);   // 60 for the first second, 30 for the second: about 45, not the 30 it ended at
    }

    [Fact]
    public void When_the_steps_are_acted_on_the_policy_stops_at_the_second()
    {
        var policy = new QualityPolicy(new QualityPolicy.Settings { MaxStep = ScreenSharer.ActedSteps });
        int highest = 0;
        for (long t = 0; t < 400_000; t += QualityPolicy.TickMs)
        {
            policy.Tick(t, new RoomQuality(4, 4, 10, 4), new OwnLink(0, 0, 4), 4);
            highest = Math.Max(highest, policy.Step);
        }
        Assert.Equal(ScreenSharer.ActedSteps, highest);
    }

    [Fact]
    public void The_setting_is_kept_by_a_retarget_and_is_on_unless_the_environment_says_otherwise()
    {
        Assert.True(new ScreenOptions().AdaptQuality || Environment.GetEnvironmentVariable("YAPARR_ADAPT") is not null);
        Assert.False(Options(adapt: false).Retarget(null, 5).AdaptQuality);
        Assert.True(Options(adapt: true).Retarget(null, 5).AdaptQuality);
    }
}
