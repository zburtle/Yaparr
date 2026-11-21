using Yaparr.AppCore;

namespace Yaparr.Tests;

public class ListTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 21, 15, 0, 0)));

    [Fact]
    public void Today_shows_the_clock_and_recent_days_show_their_name()
    {
        Assert.Equal("10:53", ListTime.Of(Now.AddHours(-4).AddMinutes(-7), Now));
        Assert.Equal("Yesterday", ListTime.Of(Now.AddDays(-1), Now));
        Assert.Equal(Now.AddDays(-3).LocalDateTime.ToString("ddd"), ListTime.Of(Now.AddDays(-3), Now));
    }

    [Fact]
    public void The_date_line_of_a_chat_says_today_yesterday_or_the_date()
    {
        Assert.Equal("Today", ListTime.DayLabel(Now.AddHours(-5), Now));
        Assert.Equal("Yesterday", ListTime.DayLabel(Now.AddDays(-1), Now));
        var earlier = Now.AddDays(-30);
        Assert.Equal(earlier.LocalDateTime.ToString("dddd, MMMM d"), ListTime.DayLabel(earlier, Now));
        var lastYear = new DateTimeOffset(2025, 12, 25, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2025, 12, 25, 12, 0, 0)));
        Assert.Equal("Thursday, December 25, 2025", ListTime.DayLabel(lastYear, Now));
    }

    [Fact]
    public void Older_messages_show_the_date_and_no_message_shows_nothing()
    {
        Assert.Equal(Now.AddDays(-20).LocalDateTime.ToString("MMM d"), ListTime.Of(Now.AddDays(-20), Now));
        var lastYear = new DateTimeOffset(2025, 12, 25, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2025, 12, 25, 12, 0, 0)));
        Assert.Equal("Dec 25, 2025", ListTime.Of(lastYear, Now));
        Assert.Equal("", ListTime.Of(DateTimeOffset.MinValue, Now));
    }
}
