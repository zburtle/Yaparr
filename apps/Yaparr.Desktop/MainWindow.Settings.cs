using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// All the settings in one place over the window: a tree of sections on the left (Network has the VPN and the TURN relay under it) and
// the section's controls on the right.
public partial class MainWindow
{
    private IInputElement? _settingsOpener;

    // The devices signed in as you are listed while that section is showing, kept up to date as they connect and disconnect.
    private readonly Avalonia.Threading.DispatcherTimer _devicesTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    // Opens the settings on a section: "devices", "notifications", "voice", "network", "vpn", "turn", "blocked", "backup" or "about".
    internal void ShowSettings(string section = "devices")
    {
        section = section.ToLowerInvariant();   // the sections are found by their lower-case names; "Voice" used to land on the first one
        if (!SettingsOverlay.IsVisible) _settingsOpener = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        AboutText.Text = $"Yaparr {Updater.CurrentVersion}\nServerless peer-to-peer chat over the BitTorrent DHT.";
        SettingsOverlay.IsVisible = true;
        var node = FindSettingsNode(SettingsTree.Items.OfType<TreeViewItem>(), section) ?? FindSettingsNode(SettingsTree.Items.OfType<TreeViewItem>(), "devices")!;
        if (SettingsTree.SelectedItem == node) ShowSettingsSection(section); else SettingsTree.SelectedItem = node;
        // Focus goes into the sections, so the arrow keys work at once.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (SettingsPicker.IsVisible ? (Control)SettingsPicker : node).Focus());
    }

    private static TreeViewItem? FindSettingsNode(IEnumerable<TreeViewItem> items, string key)
    {
        foreach (var item in items)
        {
            if (item.Tag as string == key) return item;
            if (FindSettingsNode(item.Items.OfType<TreeViewItem>(), key) is { } child) return child;
        }
        return null;
    }

    private void CloseSettings()
    {
        if (!SettingsOverlay.IsVisible) return;
        SettingsOverlay.IsVisible = false;
        _devicesTimer.Stop();
        VoiceView.SetActive(false);
        VpnPanel.SetActive(false);
        TurnPanel.SetActive(false);
        // Focus goes back to where it was, so the keyboard carries on from the same place.
        (_settingsOpener as Control)?.Focus();
        _settingsOpener = null;
    }

    private void ShowSettingsSection(string key)
    {
        _devicesTimer.Tick -= DevicesTimer_Tick;
        _devicesTimer.Stop();
        if (key == "devices")
        {
            RefreshDevices();
            _devicesTimer.Tick += DevicesTimer_Tick;
            _devicesTimer.Start();
        }
        VoiceView.SetActive(key == "voice");
        VpnPanel.SetActive(key == "vpn");
        TurnPanel.SetActive(key == "turn");
        if (key == "network") NetworkView.Activate();
        if (key == "blocked") RefreshBlocked();
        var sections = new (string Key, Control Panel)[]
        {
            ("devices", SectionDevices), ("notifications", SectionNotifications), ("voice", SectionVoice), ("network", SectionNetwork),
            ("vpn", SectionVpn), ("turn", SectionTurn), ("blocked", SectionBlocked), ("backup", SectionBackup), ("about", SectionAbout),
        };
        foreach (var (name, panel) in sections) panel.IsVisible = name == key;
    }

    private void DevicesTimer_Tick(object? sender, EventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        if (_node == null) return;
        DeviceTable.ItemsSource = _node.MyDevices
            .Select(d => d.IsThisDevice ? new DeviceRow("This device  ·  " + d.Id.Short, "this computer") : new DeviceRow(d.Id.Short, DescribeLink(d.Link)))
            .ToList();
    }

    private static string DescribeLink(PeerLink link) => link switch
    {
        PeerLink.Direct => "connected directly",
        PeerLink.Relayed => "connected through another member",
        PeerLink.ViaServer => "connected through the relay server",
        PeerLink.Reconnecting => "reconnecting",
        PeerLink.NotResponding => "connected, not responding",
        _ => "not connected",
    };

    private void Settings_Click(object? sender, RoutedEventArgs e) => ShowSettings();

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => CloseSettings();

    // Network is open whenever it, or the VPN or TURN relay under it, is the section on show. A click anywhere on its row, not only on its
    // arrow, selects it, and so opens it on the first click.
    private void SettingsTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SettingsTree).Properties.IsLeftButtonPressed || e.Source is not Avalonia.Visual source) return;
        if (source.FindAncestorOfType<ToggleButton>(includeSelf: true) != null) return;   // the arrow does its own
        if (source.FindAncestorOfType<TreeViewItem>(includeSelf: true) != NetworkNode) return;
        NetworkNode.IsExpanded = true;
    }

    private void SettingsTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SettingsTree.SelectedItem is not TreeViewItem { Tag: string key }) return;
        if (key is "network" or "vpn" or "turn") NetworkNode.IsExpanded = true;
        SelectInPicker(key);
        ShowSettingsSection(key);
    }

    private void SelectInPicker(string key)
    {
        if (SettingsPicker.SelectedItem is ComboBoxItem { Tag: string current } && current == key) return;
        SettingsPicker.SelectedItem = SettingsPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == key);
    }

    private void SettingsPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SettingsPicker.SelectedItem is not ComboBoxItem { Tag: string key }) return;
        if (SettingsTree.SelectedItem is TreeViewItem { Tag: string current } && current == key) return;
        if (FindSettingsNode(SettingsTree.Items.OfType<TreeViewItem>(), key) is { } node) SettingsTree.SelectedItem = node;
    }

    // In a narrow window the list of sections would leave too little room for the page, so it becomes a drop-down above it.
    private const double SettingsNarrow = 640;

    private void SettingsOverlay_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < SettingsNarrow;
        if (SettingsPicker.IsVisible == narrow) return;
        SettingsPicker.IsVisible = narrow;
        SettingsTree.IsVisible = !narrow;
        SettingsGrid.ColumnDefinitions[0].Width = narrow ? new GridLength(0) : new GridLength(200);
        Grid.SetColumn(SettingsPage, narrow ? 0 : 1);
        Grid.SetColumnSpan(SettingsPage, narrow ? 2 : 1);
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

/// <summary>One line of the devices table in the settings.</summary>
internal sealed record DeviceRow(string Device, string Connection);
