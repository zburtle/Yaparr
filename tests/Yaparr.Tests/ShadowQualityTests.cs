using Yaparr.Chat;
using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>The sharer's side of adaptive quality in shadow mode: what viewers report goes through the policy and what it would do is written down, and nothing else happens.</summary>
public class ShadowQualityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static ViewerQualitySample Bad => new(true, true, 12, true);
    private static ViewerQualitySample Good => new(true, false, 60, false);

    [Fact]
    public async Task When_most_of_the_room_is_in_trouble_the_sharer_says_what_it_would_do_and_changes_nothing()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        var views = await c.WatchAllAsync(source.Info.Id);
        using var sharer = new ScreenSharer(source, new ScreenOptions { Fps = 60, Kbps = 6000, MaxWidth = 1920, MaxHeight = 1080, X11Display = null, AdaptQuality = false });
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sharer.QualityDecided += said.Enqueue;
        try
        {
            views[0].QualityProvider = () => Bad;
            views[1].QualityProvider = () => Bad;
            views[2].QualityProvider = () => Good;
            sharer.StartQualityWatchForTest();
            await TestUtil.WaitFor(() => !said.IsEmpty, 60_000);
            output.WriteLine(said.First());
            Assert.Contains("shadow mode, nothing is changed", said.First());
            Assert.Contains("would lower to 1080p30 at 4.0 Mbit/s (step 1 of 3): 2 of 3 viewers lost frames for 10 s", said.First());
            Assert.Equal(1, sharer.ShadowStep);
            Assert.Equal(1, sharer.ShadowChanges);
            Assert.Contains("3 viewers reported, 2 in trouble", sharer.QualityNote);
            Assert.Contains("shadow mode", sharer.Describe());
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task One_viewer_in_trouble_among_three_changes_nothing_and_the_note_says_how_the_room_is()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 3, rootFanout: 3);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 6000);
        var views = await c.WatchAllAsync(source.Info.Id);
        using var sharer = new ScreenSharer(source, new ScreenOptions { Fps = 60, Kbps = 6000, MaxWidth = 1920, MaxHeight = 1080, X11Display = null, AdaptQuality = false });
        var said = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sharer.QualityDecided += said.Enqueue;
        try
        {
            views[0].QualityProvider = () => Bad;
            views[1].QualityProvider = () => Good;
            views[2].QualityProvider = () => Good;
            sharer.StartQualityWatchForTest();
            await TestUtil.WaitFor(() => sharer.QualityInputs.Room.Viewers == 3, 30_000);
            await Task.Delay(14_000);   // longer than the ten seconds it takes to act
            Assert.Empty(said);
            Assert.Equal(0, sharer.ShadowStep);
            Assert.Equal(0, sharer.ShadowChanges);
            Assert.Contains("3 viewers reported, 1 in trouble, worst 12 fps", sharer.QualityNote);
            Assert.Contains("would be at the quality asked for", sharer.QualityNote);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }
}
