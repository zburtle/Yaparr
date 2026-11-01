using Avalonia.Controls;

namespace Yaparr.Desktop;

// Sounds and the flashing taskbar button, for things that happen while you are looking elsewhere.
public partial class MainWindow
{
    private UiSounds _sounds = new(() => null, null);

    private void SetUpNotifications()
    {
        _sounds = new UiSounds(() => _settings.VoiceOutput, text => _log.Write(text)) { Enabled = _settings.SoundsEnabled };
        SoundsItem.IsChecked = _settings.SoundsEnabled;
        FlashItem.IsChecked = _settings.FlashTaskbar;
        SoundsItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsCheckedProperty) return;
            _settings.SoundsEnabled = _sounds.Enabled = SoundsItem.IsChecked;
            _data.SaveSettings(_settings);
        };
        FlashItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsCheckedProperty) return;
            _settings.FlashTaskbar = FlashItem.IsChecked;
            if (!_settings.FlashTaskbar) TaskbarFlash.Stop(this);
            _data.SaveSettings(_settings);
        };
        // Coming back to the window ends the flashing (Windows also does this itself when it is brought to the front).
        Activated += (_, _) => { TaskbarFlash.Stop(this); UpdateTaskbarBadge(); };

        // The sound a new message makes, to choose from: each plays when it is picked.
        _sounds.MessageStyle = _settings.MessageSound;
        foreach (var style in UiSounds.MessageStyles)
        {
            var item = new MenuItem { Header = style, ToggleType = MenuItemToggleType.Radio, GroupName = "message-sound", IsChecked = style == _settings.MessageSound };
            item.Click += (_, _) =>
            {
                _settings.MessageSound = _sounds.MessageStyle = style;
                _data.SaveSettings(_settings);
                _sounds.Preview(style);
            };
            MessageSoundMenu.Items.Add(item);
        }

        // The number of unread messages on the taskbar button.
        TaskbarCountItem.IsChecked = _settings.TaskbarCount;
        TaskbarCountItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsCheckedProperty) return;
            _settings.TaskbarCount = TaskbarCountItem.IsChecked;
            _data.SaveSettings(_settings);
            UpdateTaskbarBadge();
        };
        _conversations.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ConversationVm conv in e.NewItems)
                    conv.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ConversationVm.Unread)) UpdateTaskbarBadge(); };
            UpdateTaskbarBadge();
        };
    }

    // The total of what is unread, on the program's button in the taskbar (Windows only).
    private void UpdateTaskbarBadge()
    {
        if (!OperatingSystem.IsWindows() || _settings == null) return;
        if (TryGetPlatformHandle()?.Handle is not { } window || window == IntPtr.Zero) return;
        TaskbarBadge.Set(window, _settings.TaskbarCount ? _conversations.Sum(c => c.Unread) : 0);
    }

    /// <summary>A new message arrived. Tells you unless you are already looking at that conversation.</summary>
    private void NotifyMessage(ConversationVm conv)
    {
        bool watching = conv == _selected && IsActive;
        if (watching) return;
        _sounds.Message();
        if (_settings.FlashTaskbar && !IsActive) TaskbarFlash.Start(this);
    }
}
