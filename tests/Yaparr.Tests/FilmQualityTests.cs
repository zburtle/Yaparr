using Yaparr.Desktop;

namespace Yaparr.Tests;

public class FilmQualityTests
{
    [Theory]
    [InlineData(0, 1)]              // not measured: Standard
    [InlineData(375_000, 0)]        //  3 Mbit/s up, two viewers: Smooth
    [InlineData(1_000_000, 1)]      //  8 Mbit/s: Standard
    [InlineData(1_800_000, 2)]      // 14.4 Mbit/s: Sharp (7.2 each)
    [InlineData(3_125_000, 3)]      // 25 Mbit/s: Sharper (12.5 each)
    [InlineData(5_000_000, 4)]      // 40 Mbit/s: Best (20 each)
    [InlineData(125_000_000, 4)]    // however fast, the sharpest there is
    public void The_quality_that_starts_picked_is_the_sharpest_the_upload_carries_for_each_viewer(double bytesPerSecond, int expected) =>
        Assert.Equal(expected, FilmOptionsPanel.DefaultQuality(bytesPerSecond, viewersHint: 2));
}
