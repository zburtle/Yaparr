using Yaparr.Chat;

namespace Yaparr.Tests;

/// <summary>The message for a viewer who is the only one in trouble (docs/adaptive-quality.md): the room's state comes down the tree, and the viewer is told only when it is certain.</summary>
public class LoneStrugglerTests
{
    private static ViewerQualitySample Bad => new(true, true, 14, false);
    private static ViewerQualitySample Good => new(true, false, 60, false);
    private static readonly RoomState OnlyMe = new(Reported: 4, Troubled: 1, OwnLinkFine: true);

    // Looks once a second, as the viewer does, for the given number of seconds; returns when it first said it (seconds from the start), or null.
    private static int? FirstSaid(LoneStrugglerWatch watch, int seconds, ViewerQualitySample mine, RoomState? room)
    {
        for (int s = 1; s <= seconds; s++) if (watch.Update(s * 1000L, mine, room)) return s;
        return null;
    }

    [Fact]
    public void A_viewer_that_is_the_only_one_in_trouble_among_at_least_three_is_told_after_fifteen_seconds_and_not_before()
    {
        var watch = new LoneStrugglerWatch();
        Assert.Equal(16, FirstSaid(watch, 30, Bad, OnlyMe));   // the first look starts the count; fifteen seconds later is the sixteenth
    }

    [Theory]
    [InlineData(2, 1)]    // a room of two: the one other viewer may be in trouble too, unsaid
    [InlineData(4, 0)]    // the room says nobody is in trouble but this viewer is: its report has not come, so it is not known
    [InlineData(4, 2)]    // two in trouble: not alone
    [InlineData(4, 4)]    // everyone
    public void It_is_not_said_in_a_small_room_or_when_the_room_does_not_say_exactly_one_viewer_is_in_trouble(int reported, int troubled)
        => Assert.Null(FirstSaid(new LoneStrugglerWatch(), 60, Bad, new RoomState(reported, troubled, true)));

    [Fact]
    public void It_is_not_said_when_the_sharers_own_link_has_trouble_or_nothing_has_come_from_the_root_or_the_viewer_is_fine()
    {
        Assert.Null(FirstSaid(new LoneStrugglerWatch(), 60, Bad, OnlyMe with { OwnLinkFine = false }));
        Assert.Null(FirstSaid(new LoneStrugglerWatch(), 60, Bad, null));
        Assert.Null(FirstSaid(new LoneStrugglerWatch(), 60, Good, OnlyMe));
        Assert.Null(FirstSaid(new LoneStrugglerWatch(), 60, ViewerQualitySample.Unknown, OnlyMe));
    }

    [Fact]
    public void Anything_that_breaks_the_condition_starts_the_count_again_and_takes_the_message_away()
    {
        var watch = new LoneStrugglerWatch();
        for (int s = 1; s <= 14; s++) Assert.False(watch.Update(s * 1000L, Bad, OnlyMe));
        Assert.False(watch.Update(15_000, Bad, OnlyMe with { OwnLinkFine = false }));   // the sharer had trouble just then: the count is lost
        for (int s = 16; s <= 29; s++) Assert.False(watch.Update(s * 1000L, Bad, OnlyMe));
        Assert.True(watch.Update(31_000, Bad, OnlyMe));                                  // fifteen seconds after it began again
        Assert.True(watch.Update(32_000, Bad, OnlyMe));                                  // and it stays
        Assert.False(watch.Update(33_000, Good, OnlyMe));                                // the picture is fine again: it goes
        Assert.False(watch.Update(34_000, Bad, OnlyMe));                                 // and a new trouble has to last fifteen seconds again
    }

    [Fact]
    public async Task The_state_of_the_room_comes_down_the_tree_to_every_viewer_with_whether_the_sharers_own_link_is_fine()
    {
        // A chain: the sharer sends to one viewer, who passes it to the next, and so on, so the third viewer hears it through two relays.
        using var c = await MediaTreeTests.Cluster.StartAsync(4, fanout: 1, rootFanout: 1);
        using var source = c.Root.StartMedia(c.Rooms[0], "screen", "screen", kbps: 1000);
        var views = await c.WatchAllAsync(source.Info.Id);
        try
        {
            Assert.All(views, v => Assert.Null(v.RoomState));   // nothing has been said yet
            views[0].QualityProvider = () => Good;
            views[1].QualityProvider = () => Good;
            views[2].QualityProvider = () => Bad;
            // The first states are of the reports that had come so far; once all three have, everyone hears the whole room.
            await TestUtil.WaitFor(() => views.All(v => v.RoomState == new RoomState(3, 1, true)), 90_000);
            Assert.True(source.OwnLinkFine);

            source.OwnLinkFine = false;   // the sharer's own link has trouble: the viewers hear that too
            await TestUtil.WaitFor(() => views.All(v => v.RoomState is { OwnLinkFine: false }), 60_000);
        }
        finally { foreach (var v in views) v.Dispose(); }
    }
}
