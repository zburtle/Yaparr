using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yaparr.AndroidApp;

// The settings are a list of sections and the page of the chosen one. On a phone they take turns (the list, then a page, with the back arrow going from the page to
// the list); on a wide screen (a tablet, a phone turned on its side) they sit side by side, as on the computer, and a section is always chosen.
public partial class MainView
{
    private const double SettingsWideWidth = 640;
    private const double SettingsListWidth = 260;

    private string? _settingsSection;

    private bool SettingsWide => SettingsBody.Bounds.Width >= SettingsWideWidth;

    private Control? SettingsPage(string key) => key switch
    {
        "you" => Section_you,
        "voice" => Section_voice,
        "network" => Section_network,
        "notifications" => Section_notifications,
        "cloud" => Section_cloud,
        "backup" => Section_backup,
        "about" => Section_about,
        _ => null,
    };

    private static readonly string[] SettingsKeys = { "you", "voice", "network", "notifications", "cloud", "backup", "about" };

    // Opened from the list on the main screen: the list on a phone, the first section on a wide screen.
    private void ResetSettingsSections()
    {
        _settingsSection = null;
        SettingsBody.SizeChanged -= SettingsBodySizeChanged;
        SettingsBody.SizeChanged += SettingsBodySizeChanged;
        SettingsLayout();
    }

    private void SettingsBodySizeChanged(object? sender, SizeChangedEventArgs e) => SettingsLayout();

    private void SettingsSectionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string key }) return;
        _settingsSection = key;
        SettingsPageScroll.Offset = default;
        SettingsLayout();
    }

    /// <summary>Lays the list and the page out for the width there is. Called when a section is chosen and when the size changes.</summary>
    private void SettingsLayout()
    {
        bool wide = SettingsWide;
        if (wide && _settingsSection == null) _settingsSection = SettingsKeys[0];
        bool page = _settingsSection != null;
        foreach (var key in SettingsKeys) if (SettingsPage(key) is { } panel) panel.IsVisible = key == _settingsSection;
        SettingsListScroll.IsVisible = wide || !page;
        SettingsPageScroll.IsVisible = page;
        SettingsBody.ColumnDefinitions[0].Width = wide ? new GridLength(SettingsListWidth) : page ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        SettingsBody.ColumnDefinitions[1].Width = page ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        // On a wide screen the chosen section is marked in the list.
        foreach (var row in SettingsList.Children.OfType<Avalonia.Controls.Button>())
            row.Background = wide && (string?)row.Tag == _settingsSection ? Yaparr.AndroidApp.Theme.Brush("Line") : Avalonia.Media.Brushes.Transparent;
    }

    // Back from a section's page to the list; false when the settings are on the list (or beside it) and back should leave them.
    private bool SettingsBackToList()
    {
        if (SettingsWide || _settingsSection == null) return false;
        _settingsSection = null;
        SettingsLayout();
        return true;
    }
}
