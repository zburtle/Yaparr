using Yaparr.AppCore;

namespace Yaparr.Tests;

public class StreamInterruptionWatchTests
{
    [Fact]
    public void Nothing_is_said_before_the_first_picture_however_long_it_takes()
    {
        var watch = new StreamInterruptionWatch();
        for (long t = 0; t <= 60_000; t += 1000) Assert.Equal(InterruptionChange.None, watch.Tick(t, suppressed: false).Change);
        Assert.False(watch.IsInterrupted);
    }

    [Fact]
    public void A_picture_that_does_not_come_for_two_and_a_half_seconds_is_an_interruption_and_it_is_said_once()
    {
        var watch = new StreamInterruptionWatch();
        watch.PictureShown(10_000);
        Assert.Equal(InterruptionChange.None, watch.Tick(11_000, false).Change);
        Assert.Equal(InterruptionChange.None, watch.Tick(12_499, false).Change);
        var (change, seconds) = watch.Tick(12_500, false);
        Assert.Equal(InterruptionChange.Interrupted, change);
        Assert.Equal(2.5, seconds, 3);
        Assert.True(watch.IsInterrupted);
        // Said once: the next looks say nothing new.
        Assert.Equal(InterruptionChange.None, watch.Tick(13_500, false).Change);
        Assert.Equal(InterruptionChange.None, watch.Tick(20_000, false).Change);
    }

    [Fact]
    public void A_picture_after_an_interruption_ends_it_and_says_how_long_it_was()
    {
        var watch = new StreamInterruptionWatch();
        watch.PictureShown(0);
        Assert.Equal(InterruptionChange.Interrupted, watch.Tick(3000, false).Change);
        var (change, seconds) = watch.PictureShown(7_000);
        Assert.Equal(InterruptionChange.Resumed, change);
        Assert.Equal(7.0, seconds, 3);   // from the last picture that came
        Assert.False(watch.IsInterrupted);
        // And it can happen again.
        Assert.Equal(InterruptionChange.None, watch.PictureShown(7_016).Change);
        Assert.Equal(InterruptionChange.Interrupted, watch.Tick(10_000, false).Change);
    }

    [Fact]
    public void A_steady_stream_and_a_brief_gap_are_not_interruptions()
    {
        var watch = new StreamInterruptionWatch();
        for (long t = 0; t < 30_000; t += 16)
        {
            // a second without a picture now and then, as when the sharer starts its encoder again for a new quality level
            if (t is >= 10_000 and < 11_000) continue;
            Assert.Equal(InterruptionChange.None, watch.PictureShown(t).Change);
            if (t % 1000 < 16) Assert.Equal(InterruptionChange.None, watch.Tick(t, false).Change);
        }
        Assert.False(watch.IsInterrupted);
    }

    [Fact]
    public void While_something_else_says_what_is_wrong_the_notice_goes_away_and_stays_away()
    {
        var watch = new StreamInterruptionWatch();
        watch.PictureShown(0);
        Assert.Equal(InterruptionChange.Interrupted, watch.Tick(3000, false).Change);
        // the connection to the sharer is lost: that has its own notice
        Assert.Equal(InterruptionChange.Cleared, watch.Tick(4000, suppressed: true).Change);
        Assert.Equal(InterruptionChange.None, watch.Tick(5000, suppressed: true).Change);
        Assert.False(watch.IsInterrupted);
        // and it is said again if the picture is still missing when the other notice is over
        Assert.Equal(InterruptionChange.Interrupted, watch.Tick(6000, false).Change);
    }
}
