using Yaparr.AppCore;
using Yaparr.Desktop;

namespace Yaparr.Tests;

/// <summary>The setting that turns off the update check at start (issue #31).</summary>
public class UpdateCheckSettingTests
{
    [Theory]
    [InlineData(true, true, true)]     // the default: look at start
    [InlineData(false, true, false)]   // turned off: no contact with GitHub at start
    [InlineData(true, false, false)]   // a Flatpak or a build with no repository has nowhere to look
    [InlineData(false, false, false)]
    public void The_check_at_start_follows_the_setting_and_needs_somewhere_to_look(bool setting, bool hasRepository, bool expected) =>
        Assert.Equal(expected, MainWindow.ShouldCheckForUpdateAtStart(setting, hasRepository));

    [Fact]
    public void The_setting_is_on_by_default_and_a_choice_to_turn_it_off_is_kept()
    {
        Assert.True(new AppSettings().CheckForUpdates);
        var json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { CheckForUpdates = false });
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.CheckForUpdates);
        // Settings written before it was here have no such line, and mean the default.
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.CheckForUpdates);
    }
}
