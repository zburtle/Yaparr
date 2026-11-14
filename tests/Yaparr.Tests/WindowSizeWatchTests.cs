using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>When a share of a window is built again for a new window size (issue #102).</summary>
public class WindowSizeWatchTests
{
    private static readonly (int, int) Small = (784, 460), Big = (1184, 660);

    [Fact]
    public void A_window_that_stays_the_size_it_was_built_for_is_never_followed()
    {
        var watch = new WindowSizeWatch(Small, 0);
        for (long t = 250; t <= 20_000; t += 250) Assert.False(watch.Update(Small, t, out _));
    }

    [Fact]
    public void A_new_size_is_followed_once_it_has_held_for_half_a_second_and_the_share_has_run_long_enough()
    {
        var watch = new WindowSizeWatch(Small, 0);
        // Resized at 4 s: seen, not yet followed; after half a second it is.
        Assert.False(watch.Update(Big, 4_000, out _));
        Assert.False(watch.Update(Big, 4_250, out _));
        Assert.True(watch.Update(Big, 4_500, out var followed));
        Assert.Equal(Big, followed);
        // It is now the size built for: nothing more happens while it stays.
        Assert.False(watch.Update(Big, 5_000, out _));
        Assert.False(watch.Update(Big, 9_000, out _));
    }

    [Fact]
    public void A_window_that_is_being_dragged_is_not_followed_until_it_stops()
    {
        var watch = new WindowSizeWatch(Small, 0);
        for (int i = 0; i < 40; i++) Assert.False(watch.Update((800 + i * 10, 500 + i * 5), 5_000 + i * 250, out _));   // a new size every time it is looked at
        Assert.False(watch.Update((1200, 700), 15_000, out _));   // the first look at the last size
        Assert.True(watch.Update((1200, 700), 15_500, out var followed));
        Assert.Equal((1200, 700), followed);
    }

    [Fact]
    public void Two_restarts_are_at_least_three_seconds_apart_and_the_first_not_sooner_than_three_seconds_after_the_capture_was_built()
    {
        var watch = new WindowSizeWatch(Small, 10_000);
        Assert.False(watch.Update(Big, 10_100, out _));
        Assert.False(watch.Update(Big, 11_000, out _));       // settled, but the capture is only a second old
        Assert.True(watch.Update(Big, 13_000, out _));        // three seconds on

        // Back to the small size at once: it has to wait out the gap as well.
        Assert.False(watch.Update(Small, 13_100, out _));
        Assert.False(watch.Update(Small, 14_000, out _));
        Assert.False(watch.Update(Small, 15_999, out _));
        Assert.True(watch.Update(Small, 16_000, out var back));
        Assert.Equal(Small, back);
    }

    [Fact]
    public void A_minimised_window_has_no_size_to_follow_and_coming_back_the_same_is_nothing()
    {
        var watch = new WindowSizeWatch(Small, 0);
        for (long t = 5_000; t <= 12_000; t += 250) Assert.False(watch.Update(null, t, out _));   // minimised: nothing happens
        for (long t = 12_250; t <= 20_000; t += 250) Assert.False(watch.Update(Small, t, out _)); // restored as it was
    }

    [Fact]
    public void A_window_that_comes_back_from_minimised_at_another_size_is_followed()
    {
        var watch = new WindowSizeWatch(Small, 0);
        Assert.False(watch.Update(null, 5_000, out _));
        Assert.False(watch.Update(Big, 6_000, out _));
        Assert.True(watch.Update(Big, 6_500, out var followed));
        Assert.Equal(Big, followed);
    }

    [Fact]
    public void A_share_built_while_the_size_was_not_known_is_built_again_once_it_is()
    {
        var watch = new WindowSizeWatch(null, 0);   // the window was minimised when the share began
        Assert.False(watch.Update(null, 4_000, out _));
        Assert.False(watch.Update(Small, 5_000, out _));
        Assert.True(watch.Update(Small, 5_500, out var followed));
        Assert.Equal(Small, followed);
    }

    [Fact]
    public void A_size_that_changes_before_it_settles_starts_the_half_second_again()
    {
        var watch = new WindowSizeWatch(Small, 0);
        Assert.False(watch.Update(Big, 5_000, out _));
        Assert.False(watch.Update((1000, 600), 5_400, out _));   // another size: the clock starts over
        Assert.False(watch.Update((1000, 600), 5_800, out _));
        Assert.True(watch.Update((1000, 600), 5_900, out var followed));
        Assert.Equal((1000, 600), followed);
    }
}
