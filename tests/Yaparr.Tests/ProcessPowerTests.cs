using Yaparr.Screen;

namespace Yaparr.Tests;

public class ProcessPowerTests
{
    [Fact]
    public void A_program_that_shares_asks_windows_not_to_throttle_it_in_the_background_and_windows_says_so()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Null(ProcessPower.KeepResponsive()); return; }
        string? first = ProcessPower.KeepResponsive();
        Assert.NotNull(first);
        Assert.Equal(first, ProcessPower.KeepResponsive());   // once
        // Where Windows has the setting at all (Windows 11, and Windows 10 from 1709), it reads back as asked: speed and timer resolution both left alone.
        if (first.StartsWith("background throttling is turned off")) Assert.True(ProcessPower.IsThrottlingTurnedOff());
    }
}
