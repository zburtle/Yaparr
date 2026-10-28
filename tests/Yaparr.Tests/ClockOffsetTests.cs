using Yaparr.Chat;

namespace Yaparr.Tests;

public class ClockOffsetTests
{
    [Fact]
    public void A_slow_clock_is_corrected_for_every_message_of_that_author()
    {
        var clocks = new ClockOffsets(null);
        // Their clock is 40 s behind ours; delivery takes 50 ms.
        clocks.Observe("alice", sentMs: 1_000_000, arrivedMs: 1_040_050);
        Assert.InRange(clocks.Correct("alice", 900_000), 940_040, 940_060);
        Assert.Equal(900_000, clocks.Correct("bob", 900_000)); // nobody learned for bob yet
    }

    [Fact]
    public void The_fastest_delivery_is_the_best_estimate_and_slow_ones_barely_move_it()
    {
        var clocks = new ClockOffsets(null);
        clocks.Observe("alice", 0, 10_000);
        clocks.Observe("alice", 100_000, 100_000 + 3_000); // a quick one: clock is 3 s off
        Assert.Equal(100_000 + 3_000, clocks.Correct("alice", 100_000));
        clocks.Observe("alice", 200_000, 200_000 + 9_000); // a slow delivery
        Assert.InRange(clocks.Correct("alice", 200_000) - 200_000, 3_000, 3_500);
    }

    [Fact]
    public void Differences_beyond_five_minutes_are_ignored_as_replays()
    {
        var clocks = new ClockOffsets(null);
        clocks.Observe("alice", 0, 20 * 60_000);
        Assert.Equal(5_000, clocks.Correct("alice", 5_000));
    }

    [Fact]
    public void Offsets_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clocks-{Guid.NewGuid():N}.json");
        try
        {
            new ClockOffsets(path).Observe("alice", 1_000, 31_000);
            Assert.Equal(31_000, new ClockOffsets(path).Correct("alice", 1_000));
        }
        finally { File.Delete(path); }
    }
}
