using Yaparr.Chat;

namespace Yaparr.Tests;

/// <summary>The quality report of docs/adaptive-quality.md: a viewer judging its own picture, and the viewers' judgements folded on their way up the tree to the sharer.</summary>
public class QualityReportTests
{
    private static ViewerCounters Totals(long shown, long lost = 0, long skipped = 0, long late = 0, long dropped = 0, long gaveUp = 0) => new(shown, lost, skipped, late, dropped, gaveUp);

    /// <summary>Feeds a meter one second at a time with the same rates; returns its sample after the last second.</summary>
    private static ViewerQualitySample Run(ViewerQualityMeter meter, ref long now, ref ViewerCounters totals, int seconds, int shownPerSecond, int lostPerSecond = 0, int droppedPerSecond = 0, int gaveUpPerSecond = 0)
    {
        for (int s = 0; s < seconds; s++)
        {
            now += 1000;
            totals = new ViewerCounters(totals.Shown + shownPerSecond, totals.Lost + lostPerSecond, totals.Skipped, totals.Late, totals.DecoderDropped + droppedPerSecond, totals.GaveUp + gaveUpPerSecond);
            meter.Update(now, totals);
        }
        return meter.Sample;
    }

    [Fact]
    public void A_viewer_says_nothing_until_it_has_five_seconds_of_pictures_to_go_by()
    {
        var meter = new ViewerQualityMeter();
        long now = 0; var totals = Totals(0);
        Assert.False(Run(meter, ref now, ref totals, 4, 60).Known);
        Assert.True(Run(meter, ref now, ref totals, 2, 60).Known);
    }

    [Fact]
    public void A_picture_stream_that_arrives_whole_is_not_trouble_and_its_rate_is_the_rate_shown()
    {
        var meter = new ViewerQualityMeter();
        long now = 0; var totals = Totals(0);
        var sample = Run(meter, ref now, ref totals, 12, 58);
        Assert.True(sample.Known);
        Assert.False(sample.Troubled);
        Assert.False(sample.Lossy);
        Assert.InRange(sample.Fps, 57, 59);
    }

    [Theory]
    [InlineData(55, 5, true)]    // 5 of 60 not shown: 8%, inside the limit...
    public void Pictures_that_were_lost_make_trouble_only_from_one_in_ten_up(int shown, int lost, bool _)
    {
        var meter = new ViewerQualityMeter();
        long now = 0; var totals = Totals(0);
        Assert.False(Run(meter, ref now, ref totals, 12, shown, lost).Troubled);   // 8.3% not shown
        meter = new ViewerQualityMeter(); now = 0; totals = Totals(0);
        Assert.True(Run(meter, ref now, ref totals, 12, 50, 10).Troubled);          // 16.7%
        meter = new ViewerQualityMeter(); now = 0; totals = Totals(0);
        Assert.False(Run(meter, ref now, ref totals, 12, 54, 6).Troubled);          // exactly one in ten is the limit: shown 90% is not below 90%
        meter = new ViewerQualityMeter(); now = 0; totals = Totals(0);
        Assert.True(Run(meter, ref now, ref totals, 12, 53, 7).Troubled);           // a little more than one in ten
    }

    [Fact]
    public void A_decoder_that_drops_frames_is_trouble_and_packets_that_never_come_are_lossy()
    {
        var meter = new ViewerQualityMeter();
        long now = 0; var totals = Totals(0);
        Assert.True(Run(meter, ref now, ref totals, 12, 60, 0, droppedPerSecond: 1).Troubled);
        meter = new ViewerQualityMeter(); now = 0; totals = Totals(0);
        var sample = Run(meter, ref now, ref totals, 12, 60, 0, 0, gaveUpPerSecond: 2);
        Assert.True(sample.Lossy);
        Assert.False(sample.Troubled);
    }

    [Fact]
    public void Trouble_that_is_over_leaves_the_judgement_after_ten_seconds_and_a_stream_that_stops_says_nothing()
    {
        var meter = new ViewerQualityMeter();
        long now = 0; var totals = Totals(0);
        Assert.True(Run(meter, ref now, ref totals, 12, 40, 20).Troubled);
        Assert.False(Run(meter, ref now, ref totals, 12, 60).Troubled);
        // nothing arrives at all: nothing to judge by, which is no information
        meter = new ViewerQualityMeter(); now = 0; totals = Totals(0);
        Assert.False(Run(meter, ref now, ref totals, 12, 0).Known);
    }

    [Fact]
    public void Reports_fold_by_adding_the_counts_and_taking_the_worst_known_rate()
    {
        var a = new MediaQuality(3, 1, 20, 1);
        var b = new MediaQuality(2, 2, 12, 0);
        Assert.Equal(new MediaQuality(5, 3, 12, 1), a.Plus(b));
        Assert.Equal(new MediaQuality(5, 1, 20, 1), a.Plus(new MediaQuality(2, 0, 0, 0)));   // a rate of 0 is not known: it does not make the worst
        Assert.Equal(MediaQuality.None, MediaQuality.None.Plus(MediaQuality.None));
        Assert.Equal(new MediaQuality(1, 1, 8, 0), new ViewerQualitySample(true, true, 8, false).AsQuality);
        Assert.Equal(MediaQuality.None, ViewerQualitySample.Unknown.AsQuality);
    }

    [Fact]
    public async Task The_sharer_hears_how_many_viewers_there_are_and_how_many_are_in_trouble_from_viewers_below_relays_too()
    {
        // A chain: the sharer sends to one viewer, who passes it to the next, and so on, so the third viewer's report goes up through two relays.
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 1, rootFanout: 1);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            Assert.Equal(MediaQuality.None, source.Room);   // nobody has said anything yet
            views[0].QualityProvider = () => new ViewerQualitySample(true, false, 60, false);
            views[1].QualityProvider = () => new ViewerQualitySample(true, true, 22, true);
            views[2].QualityProvider = () => new ViewerQualitySample(true, true, 14, false);
            await TestUtil.WaitFor(() => source.Room.Viewers == 3, 40_000);
            var room = source.Room;
            Assert.Equal(3, room.Viewers);
            Assert.Equal(2, room.Troubled);
            Assert.Equal(14, room.WorstFps);
            Assert.Equal(1, room.Lossy);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task A_viewer_that_gives_no_judgement_is_not_counted_and_its_relays_still_pass_on_what_is_below()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 1, rootFanout: 1);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            // the first viewer is a version that says nothing; the second one, below it, does
            views[1].QualityProvider = () => new ViewerQualitySample(true, true, 10, false);
            views[2].QualityProvider = () => ViewerQualitySample.Unknown;   // has nothing to judge by yet: counts as nobody
            await TestUtil.WaitFor(() => source.Room.Viewers == 1, 40_000);
            Assert.Equal(new MediaQuality(1, 1, 10, 0), source.Room);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }

    [Fact]
    public async Task What_a_viewer_stopped_saying_leaves_the_room_after_a_few_seconds()
    {
        using var c = await MediaTreeTests.Cluster.StartAsync(2, fanout: 2, rootFanout: 2);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            views[0].QualityProvider = () => new ViewerQualitySample(true, true, 5, false);
            await TestUtil.WaitFor(() => source.Room.Viewers == 1, 30_000);
            views[0].QualityProvider = null;
            await TestUtil.WaitFor(() => source.Room.Viewers == 0, 30_000);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }
}
