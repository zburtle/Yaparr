using Yaparr.Chat;
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
    public void A_watch_says_what_it_decodes_and_how_many_frames_were_skipped_for_a_key_frame()
    {
        var figures = new WatchFigures("Zed's screen", 0, 0, 0, 0, Lost: 4, 0, 0, 0, 0, Attached: true, Parent: null, Missed: 100, PictureKind: "H264 1920x1080");
        var text = figures.Describe(10_000);
        Assert.Equal(96, figures.Skipped);
        Assert.Contains("H264 1920x1080", text);
        Assert.Contains("96 skipped waiting for a key frame", text);
    }

    [Fact]
    public void A_watch_that_is_not_attached_says_so()
    {
        var figures = new WatchFigures("Zed's screen", 0, 0, 0, 0, 0, 0, 0, 0, 0, Attached: false, Parent: null);
        Assert.EndsWith("not attached to the sharer", figures.Describe(5_000));
    }

    [Fact]
    public void A_watch_says_how_the_viewer_judged_its_own_picture_which_is_what_it_told_the_sharer()
    {
        WatchFigures With(ViewerQualitySample judged) => new("Zed's screen", 0, 0, 0, 0, 0, 0, 0, 0, 0, Attached: true, Parent: null, Judged: judged);

        Assert.Equal("not judged yet, so nothing was told to the sharer", With(ViewerQualitySample.Unknown).DescribeJudgement());
        Assert.Equal("judged fine, 58 pictures a second over the last ten seconds, and told the sharer so", With(new ViewerQualitySample(true, false, 58, false)).DescribeJudgement());
        Assert.Equal("judged in trouble, 22 pictures a second over the last ten seconds, losing packets for good, and told the sharer so", With(new ViewerQualitySample(true, true, 22, true)).DescribeJudgement());
        // it is in the description that is copied for a bug report too
        Assert.Contains("judged in trouble, 22 pictures a second", With(new ViewerQualitySample(true, true, 22, false)).Describe(10_000));
    }
}
