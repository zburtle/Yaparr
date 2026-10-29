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
        Activated += (_, _) => TaskbarFlash.Stop(this);
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
