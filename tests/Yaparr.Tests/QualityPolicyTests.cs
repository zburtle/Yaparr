using Yaparr.Screen;

namespace Yaparr.Tests;

/// <summary>The policy of docs/adaptive-quality.md, run with scripted rooms: a second at a time, with the clock passed in.</summary>
public class QualityPolicyTests
{
    private static readonly OwnLink FineLink = new(0, 0, 3);

    // The other rules are checked without the settle time after a change (tests of it follow), so that their numbers are those of the rules alone.
    private static readonly QualityPolicy.Settings NoSettle = new() { SettleSeconds = 0 };

    /// <summary>Runs the policy for <paramref name="seconds"/> seconds of the same room; returns the seconds (from the start of this call) at which the step changed, with the step.</summary>
    private static List<(int Second, int Step)> Run(QualityPolicy policy, ref long now, int seconds, RoomQuality room, OwnLink? own = null, int viewers = 10)
    {
        var changes = new List<(int, int)>();
        for (int s = 1; s <= seconds; s++)
        {
            now += 1000;
            var d = policy.Tick(now, room, own ?? FineLink, viewers);
            if (d.Changed) changes.Add((s, d.Step));
        }
        return changes;
    }

    [Fact]
    public void One_weak_viewer_among_ten_changes_nothing_however_long_it_lasts()
    {
        var policy = new QualityPolicy();
        long now = 0;
        var changes = Run(policy, ref now, 300, new RoomQuality(10, 1, 4, 1));
        Assert.Empty(changes);
        Assert.Equal(0, policy.Step);
        // nor one among three
        changes = Run(policy, ref now, 120, new RoomQuality(3, 1, 4, 1), viewers: 3);
        Assert.Empty(changes);
    }

    [Fact]
    public void Half_the_room_in_trouble_lowers_the_quality_after_ten_seconds_and_not_before_and_again_after_another_ten()
    {
        var policy = new QualityPolicy(NoSettle);
        long now = 0;
        var changes = Run(policy, ref now, 25, new RoomQuality(10, 5, 15, 5));
        Assert.Equal(new[] { (10, 1), (20, 2) }, changes);
    }

    [Fact]
    public void Ten_seconds_means_ten_trouble_that_stops_for_a_second_starts_the_count_again()
    {
        var policy = new QualityPolicy();
        long now = 0;
        Assert.Empty(Run(policy, ref now, 9, new RoomQuality(10, 6)));
        Assert.Empty(Run(policy, ref now, 1, new RoomQuality(10, 1)));   // one good second
        Assert.Empty(Run(policy, ref now, 9, new RoomQuality(10, 6)));
        Assert.Equal(new[] { (1, 1) }, Run(policy, ref now, 1, new RoomQuality(10, 6)));
    }

    [Theory]
    [InlineData(1, 1, true)]     // the one viewer there is is the room
    [InlineData(2, 1, false)]    // one of two: a single viewer's trouble is not the room's
    [InlineData(2, 2, true)]
    [InlineData(3, 2, true)]
    [InlineData(4, 2, true)]     // half
    [InlineData(5, 2, false)]    // less than half
    [InlineData(10, 4, false)]
    [InlineData(10, 5, true)]
    [InlineData(0, 0, false)]    // nobody reported: no information
    public void The_room_is_in_trouble_when_at_least_two_and_at_least_half_of_those_who_reported_are_or_the_one_viewer_there_is(int reported, int troubled, bool inTrouble)
        => Assert.Equal(inTrouble, QualityPolicy.RoomInTrouble(new RoomQuality(reported, troubled)));

    [Fact]
    public void A_single_viewer_in_trouble_lowers_the_quality()
    {
        var policy = new QualityPolicy();
        long now = 0;
        Assert.Equal(new[] { (10, 1) }, Run(policy, ref now, 15, new RoomQuality(1, 1, 8, 1), viewers: 1));
    }

    [Fact]
    public void Two_viewers_that_are_liars_in_a_room_of_three_can_lower_it_and_one_cannot()
    {
        var one = new QualityPolicy();
        long now = 0;
        Assert.Empty(Run(one, ref now, 60, new RoomQuality(3, 1, 0, 1), viewers: 3));
        var two = new QualityPolicy();
        now = 0;
        Assert.NotEmpty(Run(two, ref now, 60, new RoomQuality(3, 2, 0, 2), viewers: 3));   // the price of a design where the viewers hold the number
    }

    [Fact]
    public void A_quiet_half_minute_raises_it_one_step_not_before_and_the_next_after_another_half_minute()
    {
        var policy = new QualityPolicy(NoSettle);
        long now = 0;
        Run(policy, ref now, 25, new RoomQuality(10, 5));   // down to step 2
        Assert.Equal(2, policy.Step);
        Assert.Empty(Run(policy, ref now, 29, new RoomQuality(10, 0)));
        Assert.Equal(new[] { (1, 1) }, Run(policy, ref now, 1, new RoomQuality(10, 0)));
        Assert.Empty(Run(policy, ref now, 29, new RoomQuality(10, 0)));
        Assert.Equal(new[] { (1, 0) }, Run(policy, ref now, 1, new RoomQuality(10, 0)));
        Assert.Empty(Run(policy, ref now, 120, new RoomQuality(10, 0)));   // and no higher than what was asked for
    }

    [Fact]
    public void Some_viewers_in_trouble_but_not_enough_hold_the_quality_where_it_is()
    {
        var policy = new QualityPolicy();
        long now = 0;
        Run(policy, ref now, 12, new RoomQuality(10, 5));   // step 1
        Assert.Equal(1, policy.Step);
        Assert.Empty(Run(policy, ref now, 300, new RoomQuality(10, 2)));   // two of ten: not enough to lower, not quiet enough to raise
        Assert.Equal(1, policy.Step);
    }

    [Fact]
    public void A_step_up_that_is_followed_by_trouble_makes_the_next_try_wait_twice_as_long_and_so_on_to_a_quarter_of_an_hour()
    {
        var policy = new QualityPolicy(NoSettle);
        long now = 0;
        Run(policy, ref now, 11, new RoomQuality(10, 6));   // step 1
        int[] waits = new int[7];
        for (int round = 0; round < 7; round++)
        {
            // quiet until it goes up
            int quiet = 0;
            while (policy.Step == 1) { Run(policy, ref now, 1, new RoomQuality(10, 0)); quiet++; Assert.True(quiet < 1000); }
            waits[round] = quiet;
            // and trouble comes straight back
            Run(policy, ref now, 15, new RoomQuality(10, 6));
            Assert.Equal(1, policy.Step);
        }
        Assert.Equal(new[] { 30, 60, 120, 240, 480, 900, 900 }, waits);
        Assert.Equal(900, policy.UpWaitSeconds);
    }

    [Fact]
    public void The_wait_falls_back_to_half_a_minute_after_half_an_hour_without_trouble()
    {
        var policy = new QualityPolicy(NoSettle);
        long now = 0;
        Run(policy, ref now, 11, new RoomQuality(10, 6));
        Run(policy, ref now, 31, new RoomQuality(10, 0));   // up
        Run(policy, ref now, 15, new RoomQuality(10, 6));   // trouble again within the minute: the wait doubles
        Assert.Equal(60, policy.UpWaitSeconds);
        Run(policy, ref now, 1000, new RoomQuality(10, 0));
        Assert.Equal(60, policy.UpWaitSeconds);   // not yet: it is a step that failed a quarter of an hour ago
        Run(policy, ref now, 801, new RoomQuality(10, 0));
        Assert.Equal(30, policy.UpWaitSeconds);
    }

    [Fact]
    public void No_reports_is_no_information_the_quality_stays_and_nobody_watching_raises_it_again()
    {
        var policy = new QualityPolicy();
        long now = 0;
        Run(policy, ref now, 12, new RoomQuality(10, 5));   // step 1
        // five viewers that none of whose reports arrive (older versions): hold
        Assert.Empty(Run(policy, ref now, 300, RoomQuality.None, viewers: 5));
        Assert.Equal(1, policy.Step);
        // the viewers leave
        var changes = Run(policy, ref now, 40, RoomQuality.None, viewers: 0);
        Assert.Equal(new[] { (30, 0) }, changes);
    }

    [Fact]
    public void A_gap_in_the_reports_holds_the_count_of_trouble_where_it_is_and_a_long_one_starts_it_again()
    {
        // trouble for 6 s, three seconds in which nothing arrives (a report late over a bad link): the six seconds stand and the count goes on from there
        var policy = new QualityPolicy();
        long now = 0;
        Assert.Empty(Run(policy, ref now, 6, new RoomQuality(1, 1, 30, 1), viewers: 1));
        Assert.Empty(Run(policy, ref now, 3, RoomQuality.None, viewers: 1));
        Assert.Empty(Run(policy, ref now, 3, new RoomQuality(1, 1, 30, 1), viewers: 1));   // nine seconds of trouble in all
        Assert.Equal(new[] { (1, 1) }, Run(policy, ref now, 1, new RoomQuality(1, 1, 30, 1), viewers: 1));

        // a gap longer than the carry (15 s) is no information, and the count starts again after it
        var longGap = new QualityPolicy();
        now = 0;
        Run(longGap, ref now, 6, new RoomQuality(1, 1, 30, 1), viewers: 1);
        Assert.Empty(Run(longGap, ref now, 20, RoomQuality.None, viewers: 1));
        Assert.Empty(Run(longGap, ref now, 9, new RoomQuality(1, 1, 30, 1), viewers: 1));
        Assert.Equal(new[] { (1, 1) }, Run(longGap, ref now, 1, new RoomQuality(1, 1, 30, 1), viewers: 1));
    }

    [Fact]
    public void A_gap_in_the_reports_holds_the_count_of_quiet_where_it_is_too()
    {
        var policy = new QualityPolicy(NoSettle);
        long now = 0;
        Run(policy, ref now, 12, new RoomQuality(10, 6));   // step 1
        Assert.Equal(1, policy.Step);
        Assert.Empty(Run(policy, ref now, 20, new RoomQuality(10, 0)));
        Assert.Empty(Run(policy, ref now, 9, RoomQuality.None));   // the reports are late: the twenty quiet seconds stand
        Assert.Empty(Run(policy, ref now, 9, new RoomQuality(10, 0)));
        Assert.Equal(new[] { (1, 0) }, Run(policy, ref now, 1, new RoomQuality(10, 0)));
    }

    [Fact]
    public void The_sharers_own_link_lowers_it_when_it_sends_to_more_than_one_viewer_directly_and_not_when_it_sends_to_one()
    {
        var policy = new QualityPolicy();
        long now = 0;
        Assert.Equal(new[] { (10, 1) }, Run(policy, ref now, 12, RoomQuality.None, new OwnLink(0.08, 0, 3), viewers: 3));
        var one = new QualityPolicy();
        now = 0;
        Assert.Empty(Run(one, ref now, 60, RoomQuality.None, new OwnLink(0.08, 0, 1), viewers: 1));   // one direct viewer: it is that viewer's link, which the room rule sees
        var dropped = new QualityPolicy();
        now = 0;
        Assert.Equal(new[] { (10, 1) }, Run(dropped, ref now, 12, RoomQuality.None, new OwnLink(0, 40, 2), viewers: 2));
        var fine = new QualityPolicy();
        now = 0;
        Assert.Empty(Run(fine, ref now, 60, RoomQuality.None, new OwnLink(0.04, 0, 5), viewers: 5));   // 4% sent again is not enough
    }

    [Fact]
    public void At_the_bottom_of_the_ladder_it_says_so_and_stays()
    {
        var policy = new QualityPolicy(NoSettle);
        long now = 0;
        var changes = Run(policy, ref now, 200, new RoomQuality(10, 9));
        Assert.Equal(new[] { (10, 1), (20, 2), (30, 3) }, changes);
        now += 1000;
        // keep it up: the next decisions after ten more seconds say that this is as low as it goes
        QualityDecision last = default;
        for (int i = 0; i < 20; i++) { now += 1000; last = policy.Tick(now, new RoomQuality(10, 9), FineLink, 10); if (last.AtFloor) break; }
        Assert.True(last.AtFloor);
        Assert.Equal(QualityLadder.Steps - 1, last.Step);
    }

    [Fact]
    public void A_change_comes_with_its_reason_in_words()
    {
        var policy = new QualityPolicy();
        long now = 0;
        QualityDecision d = default;
        for (int i = 0; i < 10; i++) { now += 1000; d = policy.Tick(now, new RoomQuality(4, 3), FineLink, 4); }
        Assert.True(d.Changed);
        Assert.Equal("3 of 4 viewers lost frames for 10 s", d.Why);
    }

    [Fact]
    public void The_ladder_goes_down_the_presets_of_the_share_dialog_with_the_same_picture_at_70_percent_before_each_drop_in_size()
    {
        // 1080p60 at 6 Mbit/s: 1080p30, the same at 70%, then 720p30
        Assert.Equal(new QualityLevel(6000, 60, 1920, 1080), QualityLadder.For(0, 6000, 60, 1920, 1080));
        Assert.Equal(new QualityLevel(4000, 30, 1920, 1080), QualityLadder.For(1, 6000, 60, 1920, 1080));
        Assert.Equal(new QualityLevel(2800, 30, 1920, 1080), QualityLadder.For(2, 6000, 60, 1920, 1080));
        Assert.Equal(new QualityLevel(2000, 30, 1280, 720), QualityLadder.For(3, 6000, 60, 1920, 1080));
        Assert.Equal("1080p30 at 4.0 Mbit/s", QualityLadder.For(1, 6000, 60, 1920, 1080).Describe());
        Assert.Equal("1080p30 at 2.8 Mbit/s", QualityLadder.For(2, 6000, 60, 1920, 1080).Describe());
        Assert.Equal("720p30 at 2.0 Mbit/s", QualityLadder.For(3, 6000, 60, 1920, 1080).Describe());
        // 1080p30: the same at 70%, 720p30, then that at 70%
        Assert.Equal(new QualityLevel(2800, 30, 1920, 1080), QualityLadder.For(1, 4000, 30, 1920, 1080));
        Assert.Equal(new QualityLevel(2000, 30, 1280, 720), QualityLadder.For(2, 4000, 30, 1920, 1080));
        Assert.Equal(new QualityLevel(1400, 30, 1280, 720), QualityLadder.For(3, 4000, 30, 1920, 1080));
        // 720p30, the lowest: the same picture at 70%, again and again
        Assert.Equal(new[] { 2000, 1400, 980, 686 }, Enumerable.Range(0, QualityLadder.Steps).Select(s => QualityLadder.For(s, 2000, 30, 1280, 720).Kbps));
        // a share below every preset keeps its own size and rate and goes down in bitrate
        Assert.Equal(new QualityLevel(700, 15, 640, 360), QualityLadder.For(1, 1000, 15, 640, 360));
        // never below 500 kbit/s (or what was asked for, when that is lower), and no step is above the one before it
        Assert.Equal(500, QualityLadder.For(2, 800, 30, 1280, 720).Kbps);
        Assert.Equal(300, QualityLadder.For(2, 300, 30, 1280, 720).Kbps);
        Assert.Equal(4, QualityLadder.Levels(6000, 60, 1920, 1080).Count);
        foreach (var (kbps, fps, w, h) in new[] { (6000, 60, 1920, 1080), (4000, 30, 1920, 1080), (2000, 30, 1280, 720), (1000, 15, 640, 360), (800, 30, 1280, 720) })
            for (int s = 1; s < QualityLadder.Steps; s++)
            {
                var before = QualityLadder.For(s - 1, kbps, fps, w, h);
                var now = QualityLadder.For(s, kbps, fps, w, h);
                Assert.True(now.Kbps <= before.Kbps && now.Fps <= before.Fps && now.Width <= before.Width && now.Height <= before.Height, $"{kbps}/{fps}/{h} step {s}");
            }
    }

    [Fact]
    public void A_sender_that_is_behind_holds_the_quality_and_counts_nothing_until_it_is_sending_what_it_should()
    {
        var policy = new QualityPolicy();
        long now = 0;
        var behind = new OwnLink(0, 0, 1, SenderBehind: true);
        // A viewer in trouble for a minute, but the sharer itself is not sending its pictures: nothing changes, and the decision says why.
        Assert.Empty(Run(policy, ref now, 60, new RoomQuality(1, 1), behind, viewers: 1));
        now += 1000;
        Assert.True(policy.Tick(now, new RoomQuality(1, 1), behind, 1).SenderBehind);
        // Sending again: trouble counts from here (ten seconds, not ten seconds less the minute that went by).
        Assert.Empty(Run(policy, ref now, 9, new RoomQuality(1, 1), viewers: 1));
        Assert.Equal(new[] { (1, 1) }, Run(policy, ref now, 1, new RoomQuality(1, 1), viewers: 1));
        // And it is not raised again while the sender is behind, however quiet the viewers are.
        Assert.Empty(Run(policy, ref now, 90, new RoomQuality(1, 0), behind, viewers: 1));
        Assert.Equal(1, policy.Step);
    }

    [Fact]
    public void After_a_change_what_the_viewers_say_is_not_counted_for_twelve_seconds_so_one_step_is_not_three()
    {
        var policy = new QualityPolicy();
        long now = 0;
        // Trouble all the time (as a viewer's last ten seconds still say just after a change): the step down comes after ten seconds, and the next one only after
        // twelve seconds of settling and ten of trouble, not ten seconds later.
        Assert.Equal(new[] { (10, 1), (31, 2) }, Run(policy, ref now, 40, new RoomQuality(1, 1), viewers: 1));
    }

    [Fact]
    public void A_level_that_is_clean_once_the_old_figures_have_gone_stays()
    {
        var policy = new QualityPolicy();
        long now = 0;
        Assert.Equal(new[] { (10, 1) }, Run(policy, ref now, 10, new RoomQuality(1, 1), viewers: 1));
        // The viewer still says trouble for the first ten seconds or so (its window holds the level before), then the new level shows as clean.
        Assert.Empty(Run(policy, ref now, 11, new RoomQuality(1, 1), viewers: 1));
        Assert.Empty(Run(policy, ref now, 20, new RoomQuality(1, 0), viewers: 1));
        Assert.Equal(1, policy.Step);
    }

    [Fact]
    public void After_a_step_up_the_trouble_of_the_first_twelve_seconds_is_not_counted_either()
    {
        var policy = new QualityPolicy();
        long now = 0;
        Run(policy, ref now, 10, new RoomQuality(1, 1), viewers: 1);   // down to step 1
        int quiet = 0;
        while (policy.Step == 1) { Run(policy, ref now, 1, new RoomQuality(1, 0), viewers: 1); Assert.True(++quiet < 200); }
        Assert.Equal(0, policy.Step);
        Assert.Empty(Run(policy, ref now, 11, new RoomQuality(1, 1), viewers: 1));
        Assert.Equal(new[] { (10, 1) }, Run(policy, ref now, 10, new RoomQuality(1, 1), viewers: 1));
    }
}
