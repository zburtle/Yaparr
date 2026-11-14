using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Tests;

/// <summary>Which of a share's two streams a viewer watches (issue #254).</summary>
public class StreamChoiceTests
{
    private static readonly ViewerQualitySample Bad = new(true, true, 12, true), Good = new(true, false, 30, false), Unknown = default;

    // Runs the choice one second at a time from <paramref name="from"/> for <paramref name="seconds"/>, moving whenever it says so; returns where it ended and when.
    private static (WatchedStream Stream, long At) Run(StreamChoice choice, WatchedStream stream, long from, int seconds, ViewerQualitySample sample,
        bool alone = false, bool available = true)
    {
        long t = from;
        for (int s = 0; s < seconds; s++, t += 1000)
        {
            var next = choice.Update(t, available, stream, sample, alone);
            if (next != stream) return (next, t);
        }
        return (stream, t);
    }

    [Fact]
    public void Ten_seconds_of_trouble_move_a_viewer_to_the_lower_stream()
    {
        var choice = new StreamChoice();
        Assert.Equal(WatchedStream.Main, Run(choice, WatchedStream.Main, 0, 9, Bad).Stream);
        var (stream, at) = Run(choice, WatchedStream.Main, 9_000, 5, Bad);
        Assert.Equal(WatchedStream.Low, stream);
        Assert.Equal(10_000, at);
        Assert.Equal("your connection lost pictures for 10 seconds", choice.Reason);
    }

    [Fact]
    public void A_moment_of_trouble_moves_nothing()
    {
        var choice = new StreamChoice();
        Run(choice, WatchedStream.Main, 0, 6, Bad);
        Run(choice, WatchedStream.Main, 6_000, 1, Good);   // fine again: the count starts over
        Assert.Equal(WatchedStream.Main, Run(choice, WatchedStream.Main, 7_000, 9, Bad).Stream);
    }

    [Fact]
    public void A_viewer_told_it_is_the_only_one_in_trouble_moves_at_once()
    {
        var choice = new StreamChoice();
        var (stream, at) = Run(choice, WatchedStream.Main, 0, 3, Bad, alone: true);
        Assert.Equal(WatchedStream.Low, stream);
        Assert.Equal(0, at);
        Assert.Equal("your connection is the only one struggling with the full picture", choice.Reason);
    }

    [Fact]
    public void After_a_quiet_minute_on_the_lower_stream_the_full_one_is_tried_and_a_try_that_fails_doubles_the_wait()
    {
        var choice = new StreamChoice();
        var (stream, t) = Run(choice, WatchedStream.Main, 0, 20, Bad);
        Assert.Equal(WatchedStream.Low, stream);
        Run(choice, WatchedStream.Low, t, 3, Unknown);             // the new stream is not judged yet: nothing moves
        (stream, t) = Run(choice, WatchedStream.Low, t + 3_000, 120, Good);
        Assert.Equal(WatchedStream.Main, stream);                  // tried again after 60 s quiet
        Assert.Equal(StreamChoice.FirstWaitMs, choice.WaitMs);

        // The try fails at once: down again, and the next wait is twice as long.
        (stream, t) = Run(choice, WatchedStream.Main, t, 20, Bad);
        Assert.Equal(WatchedStream.Low, stream);
        Assert.Equal(2 * StreamChoice.FirstWaitMs, choice.WaitMs);
        Assert.Equal(WatchedStream.Low, Run(choice, WatchedStream.Low, t, 100, Good).Stream);   // not after 60 s this time
        Assert.Equal(WatchedStream.Main, Run(choice, WatchedStream.Low, t + 100_000, 30, Good).Stream);
    }

    [Fact]
    public void The_wait_never_grows_beyond_its_limit_and_a_long_stretch_on_the_full_stream_forgets_the_failures()
    {
        var choice = new StreamChoice();
        long t = 0;
        for (int i = 0; i < 12; i++)
        {
            (_, t) = Run(choice, WatchedStream.Main, t, 30, Bad);
            (_, t) = Run(choice, WatchedStream.Low, t, 2000, Good);
        }
        Assert.Equal(StreamChoice.LongestWaitMs, choice.WaitMs);
        Run(choice, WatchedStream.Main, t, (int)(StreamChoice.ForgetMs / 1000) + 2, Good);
        Assert.Equal(StreamChoice.FirstWaitMs, choice.WaitMs);
    }

    [Fact]
    public void Without_a_lower_stream_the_full_one_is_watched_and_one_that_is_taken_away_sends_the_viewer_back()
    {
        var choice = new StreamChoice();
        Assert.Equal(WatchedStream.Main, Run(choice, WatchedStream.Main, 0, 30, Bad, available: false).Stream);   // the sharer has not started it (yet)
        Assert.Equal(WatchedStream.Main, choice.Update(40_000, false, WatchedStream.Low, Good, false));
        Assert.Equal("the sharer stopped the lower-quality stream", choice.Reason);
    }
}
