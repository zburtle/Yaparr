using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yaparr.Desktop;

// All the settings in one place over the window: a list of sections on the left and the section's controls on the right. The things that
// still have a window of their own (voice settings, VPN, TURN, devices, the connection check) are opened from their section.
public partial class MainWindow
{
    private static readonly string[] SettingsSectionNames = { "Profile", "Devices", "Notifications", "Voice", "Network", "Updates", "About" };

    private IInputElement? _settingsOpener;

    // Opens the settings on a section: "Profile", "Devices", "Notifications", "Voice", "Network", "Updates" or "About".
    internal void ShowSettings(string section = "Profile")
    {
        if (!SettingsOverlay.IsVisible) _settingsOpener = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        AboutText.Text = $"Yaparr {Updater.CurrentVersion}\nServerless peer-to-peer chat over the BitTorrent DHT.";
        SettingsOverlay.IsVisible = true;
        int index = Math.Max(0, Array.IndexOf(SettingsSectionNames, section));
        SettingsSections.SelectedIndex = index;
        ShowSettingsSection(index);
        SettingsOverlay.Focus();
    }

    private void CloseSettings()
    {
        if (!SettingsOverlay.IsVisible) return;
        SettingsOverlay.IsVisible = false;
        // Focus goes back to where it was, so the keyboard carries on from the same place.
        (_settingsOpener as Control)?.Focus();
        _settingsOpener = null;
    }

    private void ShowSettingsSection(int index)
    {
        var sections = new Control[] { SectionProfile, SectionDevices, SectionNotifications, SectionVoice, SectionNetwork, SectionUpdates, SectionAbout };
        for (int i = 0; i < sections.Length; i++) sections[i].IsVisible = i == index;
    }

    private void Settings_Click(object? sender, RoutedEventArgs e) => ShowSettings();

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => CloseSettings();

    private void SettingsSections_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SettingsSections.SelectedIndex >= 0) ShowSettingsSection(SettingsSections.SelectedIndex);
    }

    // A click on the dark area around the panel puts the settings away; a click on the panel itself does not reach it.
    private void SettingsOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == SettingsOverlay) CloseSettings();
    }

    private void SettingsOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseSettings();
        e.Handled = true;
    }

    // Ctrl+, opens the settings from anywhere in the window.
    private void OnSettingsKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control || e.Key != Key.OemComma) return;
        e.Handled = true;
        if (SettingsOverlay.IsVisible) CloseSettings(); else ShowSettings();
    }
}
