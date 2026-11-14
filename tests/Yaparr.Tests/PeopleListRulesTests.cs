using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Tests;

/// <summary>Someone who has not been on for two weeks is no longer in the list of people.</summary>
public class PeopleListRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_list_forgets_people_after_two_weeks()
    {
        Assert.Equal(TimeSpan.FromDays(14), PeopleListRules.StaleAfter);
        Assert.True(PeopleListRules.ShowsInList(false, Now.AddDays(-1), Now, RoomRole.Member));
        Assert.True(PeopleListRules.ShowsInList(false, Now.AddDays(-13).AddHours(-23), Now, RoomRole.Member));
        Assert.True(PeopleListRules.ShowsInList(false, Now.AddDays(-14), Now, RoomRole.Member));                       // exactly two weeks: still there
        Assert.False(PeopleListRules.ShowsInList(false, Now.AddDays(-14).AddSeconds(-1), Now, RoomRole.Member));        // a moment past it: gone
        Assert.False(PeopleListRules.ShowsInList(false, Now.AddDays(-200), Now, RoomRole.Member));
    }

    [Fact]
    public void Someone_who_is_on_is_always_listed_and_comes_back_into_the_list_as_soon_as_they_are_on()
    {
        Assert.True(PeopleListRules.ShowsInList(true, Now.AddDays(-200), Now, RoomRole.Member));
        // Seen again just now: it is the last time seen that moved, so the same rule lists them.
        Assert.True(PeopleListRules.ShowsInList(false, Now, Now, RoomRole.Member));
    }

    [Theory]
    [InlineData(RoomRole.Owner)]
    [InlineData(RoomRole.Admin)]
    public void The_owner_and_the_admins_stay_listed_however_long_they_have_been_away(RoomRole role) =>
        Assert.True(PeopleListRules.ShowsInList(false, Now.AddDays(-400), Now, role));

    [Fact]
    public void A_last_seen_time_in_the_future_from_a_clock_that_is_off_does_not_hide_anyone() =>
        Assert.True(PeopleListRules.ShowsInList(false, Now.AddHours(3), Now, RoomRole.Member));
}
