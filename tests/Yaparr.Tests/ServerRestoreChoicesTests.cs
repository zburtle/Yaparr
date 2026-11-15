using Yaparr.AppCore;

namespace Yaparr.Tests;

// Choosing which saved copy of the backup to bring back from a server.
public class ServerRestoreChoicesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<ServerBackupVersion> Three() => new[]
    {
        new ServerBackupVersion(3, 2_621_440, Noon),                          // 2.5 MiB
        new ServerBackupVersion(5, 3_145_728, Noon.AddHours(5)),              // listed out of order on purpose
        new ServerBackupVersion(4, 900, Noon.AddHours(2)),
    };

    [Fact]
    public void The_copies_are_listed_newest_first_with_their_time_and_size()
    {
        var choices = ServerRestoreChoices.Describe(Three(), TimeZoneInfo.Utc);
        Assert.Equal(new[] { 5, 4, 3 }, choices.Select(c => c.Version));
        Assert.Equal("Version 5  ·  2026-11-15 17:00  ·  3.0 MiB  ·  newest", choices[0].Label);
        Assert.Equal("Version 4  ·  2026-11-15 14:00  ·  1 KiB", choices[1].Label);
        Assert.Equal("Version 3  ·  2026-11-15 12:00  ·  2.5 MiB", choices[2].Label);
        Assert.True(choices[0].Newest);
        Assert.False(choices[1].Newest);
    }

    [Fact]
    public void The_time_is_shown_in_the_zone_asked_for()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        Assert.Contains("2026-11-15 14:00", ServerRestoreChoices.Describe(new[] { new ServerBackupVersion(1, 10, Noon) }, plusTwo)[0].Label);
    }

    [Fact]
    public void The_newest_is_selected_to_begin_with()
    {
        Assert.Equal(5, ServerRestoreChoices.Preselect(Three()));
        Assert.Equal(0, ServerRestoreChoices.Preselect(Array.Empty<ServerBackupVersion>()));
    }

    [Fact]
    public void After_a_copy_that_could_not_be_opened_the_next_older_one_is_selected()
    {
        Assert.Equal(4, ServerRestoreChoices.Preselect(Three(), failed: 5));
        Assert.Equal(3, ServerRestoreChoices.Preselect(Three(), failed: 4));
        Assert.Equal(5, ServerRestoreChoices.Preselect(Three(), failed: 3));      // nothing older: back to the newest
    }

    [Fact]
    public void A_gap_in_the_numbers_is_no_problem_because_old_copies_are_removed()
    {
        var versions = new[] { new ServerBackupVersion(9, 100, Noon), new ServerBackupVersion(6, 100, Noon.AddDays(-1)), new ServerBackupVersion(2, 100, Noon.AddDays(-3)) };
        Assert.Equal(6, ServerRestoreChoices.Preselect(versions, failed: 9));
        Assert.Equal(2, ServerRestoreChoices.Preselect(versions, failed: 6));
    }
}
