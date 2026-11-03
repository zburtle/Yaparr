using Yaparr.Desktop;

namespace Yaparr.Tests;

public class ReadMarksTests
{
    [Fact]
    public void A_channel_seen_for_the_first_time_has_everything_so_far_counted_as_seen()
    {
        long clock = 1_000;
        var marks = new ReadMarks(new Dictionary<string, long>(), () => clock);

        Assert.False(marks.IsUnread("room", "general", messageTime: 900));     // old history is not unread
        Assert.False(marks.IsUnread("room", "general", messageTime: 1_000));
        Assert.True(marks.IsUnread("room", "general", messageTime: 1_001));    // what comes after is
        Assert.True(marks.Dirty);                                              // the first sight was recorded
    }

    [Fact]
    public void What_is_newer_than_the_last_time_the_channel_was_seen_is_unread_and_reading_moves_the_mark_forward_only()
    {
        long clock = 5_000;
        var marks = new ReadMarks(new Dictionary<string, long>(), () => clock);
        marks.Mark("room", "general", at: 2_000);

        Assert.False(marks.IsUnread("room", "general", 1_500));
        Assert.True(marks.IsUnread("room", "general", 2_500));

        marks.Mark("room", "general", at: 3_000);
        Assert.False(marks.IsUnread("room", "general", 2_500));
        marks.Mark("room", "general", at: 1_000);                              // an older time never moves it back
        Assert.Equal(3_000, marks.Of("room", "general"));
        marks.Mark("room", "general");                                         // now
        Assert.Equal(5_000, marks.Of("room", "general"));
    }

    [Fact]
    public void Each_channel_of_each_room_has_a_mark_of_its_own()
    {
        var marks = new ReadMarks(new Dictionary<string, long>(), () => 10_000);
        marks.Mark("one", "general", at: 4_000);
        marks.Mark("one", "games", at: 8_000);
        marks.Mark("two", "general", at: 6_000);

        Assert.True(marks.IsUnread("one", "general", 5_000));
        Assert.False(marks.IsUnread("one", "games", 5_000));
        Assert.False(marks.IsUnread("two", "general", 5_000));
        Assert.True(marks.IsUnread("two", "general", 7_000));
    }

    [Fact]
    public void The_marks_survive_a_restart_because_they_live_in_the_settings()
    {
        var saved = new Dictionary<string, long>();
        var before = new ReadMarks(saved, () => 1_000);
        before.Mark("room", "general", at: 4_000);
        Assert.True(before.Dirty);
        before.Saved();
        Assert.False(before.Dirty);

        // The app is started again with what was written: a message from while it was closed is unread, an earlier one is not.
        var after = new ReadMarks(new Dictionary<string, long>(saved), () => 9_000);
        Assert.True(after.IsUnread("room", "general", messageTime: 4_500));
        Assert.False(after.IsUnread("room", "general", messageTime: 3_500));
        Assert.False(after.Dirty);                                             // reading marks that already exist changes nothing
    }
}
