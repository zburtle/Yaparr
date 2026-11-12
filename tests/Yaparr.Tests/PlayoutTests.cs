using Yaparr.Desktop;

namespace Yaparr.Tests;

public class PlayoutTests
{
    // Arrival times (milliseconds) of a stream of the given rate, each picture off its slot by up to the jitter either way.
    private static List<double> Jittered(double fps, double jitter, int seconds = 30, int seed = 3)
    {
        var random = new Random(seed);
        double frame = 1000 / fps;
        return Enumerable.Range(0, (int)(seconds * fps)).Select(k => k * frame + (jitter == 0 ? 0 : (random.NextDouble() * 2 - 1) * jitter)).ToList();
    }

    // A capture that stalls for a while and then hands over everything it missed in a burst, every so often.
    private static List<double> Stalling(double fps, double everySeconds, double stallMs, int seconds = 30)
    {
        var arrivals = new List<double>();
        double frame = 1000 / fps, nextStall = everySeconds * 1000;
        for (int k = 0; k * frame < seconds * 1000;)
        {
            double due = k * frame;
            if (due >= nextStall)
            {
                for (int j = 0; j <= (int)(stallMs / frame); j++) { arrivals.Add(due + stallMs + j); k++; }
                nextStall += everySeconds * 1000;
            }
            else { arrivals.Add(due); k++; }
        }
        return arrivals;
    }

    // Plays the arrivals through a queue with a draw timer that ticks every tickMs, and says how many were drawn a second.
    private static (double Drawn, double Late) Play(List<double> arrivals, double tickMs, int seconds = 30)
    {
        var queue = new PlayoutQueue<object>(_ => { });
        arrivals.Sort();
        int i = 0, drawn = 0;
        for (double t = 0; t < seconds * 1000; t += tickMs)
        {
            while (i < arrivals.Count && arrivals[i] <= t) queue.Arrive(new object(), (long)arrivals[i++]);
            if (queue.IsEmpty) { queue.Reset(); continue; }
            if (queue.TryDraw((long)t, out _)) drawn++;
        }
        return (drawn / (double)seconds, queue.Late / (double)seconds);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(15.6)]   // Windows' timer does not tick faster than this unless asked to
    public void Sixty_pictures_a_second_arriving_a_frame_early_or_late_are_all_drawn(double tickMs)
    {
        // A viewer was seen to draw 49 of the 60 that arrived, with nothing lost, because the clock ran too slow for bunched arrivals.
        var (drawn, late) = Play(Jittered(60, 16), tickMs);
        Assert.InRange(drawn, 57, 61);
        Assert.True(late < 3, $"{late:0.0} a second were skipped as late");
    }

    [Theory]
    [InlineData(30, 16)]
    [InlineData(60, 0)]
    [InlineData(60, 8)]
    [InlineData(120, 4)]   // the old clock drew about half of these
    [InlineData(144, 4)]
    [InlineData(10, 30)]
    public void Every_picture_of_a_steady_stream_is_drawn_whatever_the_rate_and_jitter(double fps, double jitter)
    {
        var (drawn, _) = Play(Jittered(fps, jitter), 4);
        Assert.InRange(drawn, fps * 0.94, fps * 1.02);
    }

    [Fact]
    public void A_stall_and_the_burst_after_it_cost_a_few_pictures_not_a_fifth_of_them()
    {
        var (drawn, late) = Play(Stalling(60, 2, 100), 4);
        Assert.InRange(drawn, 55, 61);
        Assert.True(late < 5, $"{late:0.0} a second were skipped as late");
    }

    [Fact]
    public void The_pace_is_the_pictures_that_came_in_the_last_second_over_the_time_they_took_however_bunched_they_were()
    {
        var clock = new PlayoutClock();
        // Pairs 1 ms apart and then 32 ms: 33 ms for two pictures, so 16.5 ms each.
        long t = 0;
        for (int i = 0; i < 80; i++) { clock.Arrived(t); clock.Arrived(t + 1); t += 33; }
        Assert.Equal(16.5, clock.IntervalMs, 0.5);
    }

    [Theory]
    [InlineData(60, 16.67)]
    [InlineData(30, 33.33)]
    [InlineData(120, 8.33)]
    [InlineData(144, 6.94)]
    public void A_steady_stream_gives_its_own_interval(double fps, double expectedMs)
    {
        var clock = new PlayoutClock();
        foreach (var arrival in Jittered(fps, 0, seconds: 3)) clock.Arrived((long)arrival);
        Assert.Equal(expectedMs, clock.IntervalMs, 1.0);
    }

    [Fact]
    public void Until_enough_pictures_have_come_the_pace_is_thirty_a_second_and_a_slow_stream_does_not_change_it()
    {
        var clock = new PlayoutClock();
        Assert.Equal(33, clock.IntervalMs);
        for (int i = 0; i < 5; i++) clock.Arrived(i * 500);   // two a second: fewer than eight in a second
        Assert.Equal(33, clock.IntervalMs);
    }

    [Fact]
    public void A_stream_that_stops_and_starts_again_finds_its_pace_afresh()
    {
        var clock = new PlayoutClock();
        for (int i = 0; i < 100; i++) clock.Arrived(i * 33L);                 // 30 a second
        for (int i = 0; i < 100; i++) clock.Arrived(60_000 + i * 17L);        // after a long pause, 60 a second
        Assert.Equal(17, clock.IntervalMs, 1.0);
    }

    [Fact]
    public void The_pace_stays_within_what_can_be_drawn()
    {
        var clock = new PlayoutClock();
        for (int i = 0; i < 200; i++) clock.Arrived(i);   // 1000 a second
        Assert.True(clock.IntervalMs >= 4);
    }

    [Fact]
    public void Pictures_that_are_skipped_are_given_back_and_counted()
    {
        var released = new List<object>();
        var queue = new PlayoutQueue<object>(released.Add);
        for (int i = 0; i < 9; i++) queue.Arrive(new object(), i);   // more than the queue holds
        Assert.Equal(PlayoutQueue<object>.MaxQueue, queue.Count);
        Assert.Equal(4, queue.Late);
        Assert.Equal(4, released.Count);
    }

    [Fact]
    public void Drawing_waits_for_a_couple_of_pictures_or_a_short_time()
    {
        var queue = new PlayoutQueue<object>(_ => { });
        queue.Arrive(new object(), 1000);
        Assert.False(queue.TryDraw(1010, out _));     // one picture, only 10 ms since it came
        Assert.True(queue.TryDraw(1125, out _));      // alone for more than 120 ms: start anyway
        queue.Arrive(new object(), 2000);
        queue.Arrive(new object(), 2005);
        Assert.True(queue.TryDraw(2005, out _));      // two waiting: start at once
    }
}
