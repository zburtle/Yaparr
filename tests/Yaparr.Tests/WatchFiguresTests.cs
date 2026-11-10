using Yaparr.Desktop;

namespace Yaparr.Tests;

public class WatchFiguresTests
{
    [Fact]
    public void A_watch_is_described_with_what_arrived_what_was_shown_and_what_was_lost()
    {
        var figures = new WatchFigures("Zed's screen", StartedAt: 1_000, Shown: 300, FramesIn: 600, BytesIn: 1_250_000, Lost: 4, Recovered: 12, Late: 7, Dropped: 2,
            DecodeQueue: 3, Attached: true, Parent: null);

        var text = figures.Describe(now: 11_000, path: "direct");

        Assert.Contains("watched 10 s", text);
        Assert.Contains("300 pictures drawn (30.0/s on average)", text);
        Assert.Contains("600 frames arrived (60.0/s, 1000 kbit/s on average)", text);
        Assert.Contains("4 frames lost", text);
        Assert.Contains("7 too late to show", text);
        Assert.Contains("2 dropped by the decoder", text);
        Assert.Contains("attached to the sharer (direct)", text);
    }

    [Fact]
    public void A_watch_that_is_not_attached_says_so()
    {
        var figures = new WatchFigures("Zed's screen", 0, 0, 0, 0, 0, 0, 0, 0, 0, Attached: false, Parent: null);
        Assert.EndsWith("not attached to the sharer", figures.Describe(5_000));
    }
}
