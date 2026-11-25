using Avalonia.Controls;
using Avalonia.Styling;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

// Light, dark, or the system's own, chosen in the settings under Appearance.
public partial class MainWindow
{
    private void SetUpAppearance()
    {
        _settings.Theme = ThemeChoice.Normalize(_settings.Theme);
        ApplyTheme();
        var items = new[] { ThemeSystemItem, ThemeLightItem, ThemeDarkItem };
        foreach (var item in items) item.IsChecked = (string?)item.Tag == _settings.Theme;
        foreach (var item in items)
            item.IsCheckedChanged += (_, _) =>
            {
                if (item.IsChecked != true || item.Tag is not string choice || choice == _settings.Theme) return;
                _settings.Theme = choice;
                _data.SaveSettings(_settings);
                ApplyTheme();
            };
    }

    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app) app.RequestedThemeVariant = ThemeVariantFor(_settings.Theme);
    }

    /// <summary>The variant a choice stands for: the system's own (Default) unless light or dark was chosen.</summary>
    internal static ThemeVariant ThemeVariantFor(string? choice) => ThemeChoice.Normalize(choice) switch
    {
        ThemeChoice.Light => ThemeVariant.Light,
        ThemeChoice.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
