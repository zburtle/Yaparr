using Yaparr.Desktop;

namespace Yaparr.Tests;

public class PresenceTrackerTests
{
    [Fact]
    public void Who_was_already_there_is_not_announced_and_later_arrivals_and_departures_are()
    {
        var tracker = new PresenceTracker(new[] { "a", "b" });

        var first = tracker.Update(new[] { "a", "b" });
        Assert.Empty(first.Arrived);
        Assert.Empty(first.Gone);

        var second = tracker.Update(new[] { "b", "c" });
        Assert.Equal(new[] { "c" }, second.Arrived);
        Assert.Equal(new[] { "a" }, second.Gone);

        var third = tracker.Update(new[] { "b", "c" });
        Assert.Empty(third.Arrived);
        Assert.Empty(third.Gone);
    }

    [Fact]
    public void A_tracker_that_starts_empty_announces_the_first_arrival()
    {
        var tracker = new PresenceTracker();
        Assert.Empty(tracker.Update(Array.Empty<string>()).Arrived);
        Assert.Single(tracker.Update(new[] { "x" }).Arrived);
        Assert.Single(tracker.Update(Array.Empty<string>()).Gone);
    }
}
