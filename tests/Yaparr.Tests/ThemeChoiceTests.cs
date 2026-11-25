using Yaparr.AppCore;

namespace Yaparr.Tests;

public class ThemeChoiceTests
{
    [Theory]
    [InlineData("System", "System")]
    [InlineData("Light", "Light")]
    [InlineData("Dark", "Dark")]
    [InlineData("dark", "Dark")]
    [InlineData(" LIGHT ", "Light")]
    [InlineData("", "System")]
    [InlineData(null, "System")]
    [InlineData("Sepia", "System")]
    public void A_choice_is_kept_or_falls_back_to_the_system_one(string? given, string expected) =>
        Assert.Equal(expected, ThemeChoice.Normalize(given));

    [Fact]
    public void A_new_profile_follows_the_system() => Assert.Equal(ThemeChoice.System, new AppSettings().Theme);

    [Fact]
    public void The_choice_survives_the_settings_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData(dir);
            var settings = data.LoadSettings();
            settings.Theme = ThemeChoice.Dark;
            data.SaveSettings(settings);
            Assert.Equal(ThemeChoice.Dark, data.LoadSettings().Theme);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
