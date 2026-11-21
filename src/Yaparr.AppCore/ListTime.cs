namespace Yaparr.AppCore;

/// <summary>The time shown beside a chat's latest message in the list: the clock for today, the day's name within the last week, the date after that.</summary>
public static class ListTime
{
    /// <summary>The date line above the first message of a day in a chat: "Today", "Yesterday", "Monday, March 3", with the year when it is not this one.</summary>
    public static string DayLabel(DateTimeOffset when, DateTimeOffset now)
    {
        var day = when.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        return day.Year == today.Year ? day.ToString("dddd, MMMM d") : day.ToString("dddd, MMMM d, yyyy");
    }

    public static string Of(DateTimeOffset when, DateTimeOffset now)
    {
        if (when <= DateTimeOffset.MinValue.AddDays(1)) return "";
        var local = when.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var days = (today - local.Date).Days;
        if (days <= 0) return local.ToString("HH:mm");
        if (days == 1) return "Yesterday";
        if (days < 7) return local.ToString("ddd");
        return local.Year == today.Year ? local.ToString("MMM d") : local.ToString("MMM d, yyyy");
    }
}
