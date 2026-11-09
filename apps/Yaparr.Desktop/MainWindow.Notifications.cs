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
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            _settings.SoundsEnabled = _sounds.Enabled = SoundsItem.IsChecked == true;
            _data.SaveSettings(_settings);
        };
        FlashItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            _settings.FlashTaskbar = FlashItem.IsChecked == true;
            if (!_settings.FlashTaskbar) TaskbarFlash.Stop(this);
            _data.SaveSettings(_settings);
        };
        // Coming back to the window ends the flashing (Windows also does this itself when it is brought to the front).
        Activated += (_, _) => { TaskbarFlash.Stop(this); UpdateTaskbarBadge(); };

        // The sound a new message makes, to choose from: each plays when it is picked.
        _sounds.MessageStyle = _settings.MessageSound;
        MessageSoundBox.ItemsSource = UiSounds.MessageStyles;
        MessageSoundBox.SelectedItem = UiSounds.MessageStyles.Contains(_settings.MessageSound) ? _settings.MessageSound : UiSounds.MessageStyles[0];
        MessageSoundBox.SelectionChanged += (_, _) =>
        {
            if (MessageSoundBox.SelectedItem is not string style || style == _settings.MessageSound) return;
            _settings.MessageSound = _sounds.MessageStyle = style;
            _data.SaveSettings(_settings);
            _sounds.Preview(style);
        };

        // Pop-ups in the desktop's notification area (Linux).
        PopupsItem.IsVisible = LinuxDesktop.Available || WindowsToast.Available;
        PopupsItem.IsChecked = _settings.DesktopPopups;
        PopupsItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            _settings.DesktopPopups = PopupsItem.IsChecked == true;
            _data.SaveSettings(_settings);
        };

        // Busy rooms: only being named, answered or written to directly makes a sound.
        MentionsOnlyItem.IsChecked = _settings.NotifyOnlyMentions;
        MentionsOnlyItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            _settings.NotifyOnlyMentions = MentionsOnlyItem.IsChecked == true;
            _data.SaveSettings(_settings);
        };

        // The number of unread messages on the taskbar button.
        TaskbarCountItem.IsChecked = _settings.TaskbarCount;
        TaskbarCountItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty) return;
            _settings.TaskbarCount = TaskbarCountItem.IsChecked == true;
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
        if (_settings == null) return;
        if (LinuxDesktop.Available) { LinuxDesktop.SetUnread(_settings.TaskbarCount ? _conversations.Sum(c => c.Unread) : 0, text => _log.Write(text)); return; }
        if (!OperatingSystem.IsWindows()) return;
        if (TryGetPlatformHandle()?.Handle is not { } window || window == IntPtr.Zero) return;
        TaskbarBadge.Set(window, _settings.TaskbarCount ? _conversations.Sum(c => c.Unread) : 0);
    }

    /// <summary>
    /// A new message arrived. Tells you unless you are already looking at it: the window is active and the message is in the
    /// conversation and the text channel on show. Being named or answered has a sound of its own that nothing holds back; with
    /// "only for mentions" on, other messages in rooms are silent (direct messages still count).
    /// </summary>
    private void NotifyMessage(ConversationVm conv, MessageVm message)
    {
        bool watching = conv == _selected && IsActive && (message.Channel == null || message.Channel == conv.CurrentChannel);
        if (watching) return;
        if (message.Highlight) _sounds.Mentioned();
        else if (_settings.NotifyOnlyMentions && !conv.IsDirect) return;
        else _sounds.Message();
        if (_settings.FlashTaskbar && !IsActive) TaskbarFlash.Start(this);
        if (_settings.DesktopPopups && !IsActive) ShowPopup(conv, message);
    }

    private void ShowPopup(ConversationVm conv, MessageVm message)
    {
        var who = message.Highlight ? $"{message.Sender} named you in {conv.Title}" : conv.IsDirect ? message.Sender : $"{message.Sender} in {conv.Title}";
        var text = message.HasAttachment && !message.HasText ? "Sent a picture" : message.Text;
        text = text.Length > 140 ? text[..139] + "…" : text;
        if (LinuxDesktop.Available) LinuxDesktop.Notify(who, text, t => _log.Write(t));
        else if (WindowsToast.Available && TryGetPlatformHandle()?.Handle is { } window)
            WindowsToast.Show(window, who, text, () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ShowFromTray();
                SelectConversation(conv);
            }));
    }

    // Goes to the oldest message that names you or answers you and that you have not seen yet, in whichever room it is.
    private void GoToFirstMention_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (ConversationVm Conv, MessageVm Message)? first = null;
        foreach (var conv in _conversations.Where(c => c.Mentioned))
        {
            var room = RoomFor(conv);
            var unseen = room != null
                ? conv.AllMessages.Where(m => m.Highlight && m.Channel is { } ch && Marks.IsUnread(room.RoomId, ch, m.Time.ToUnixTimeMilliseconds()))
                : conv.Messages.Skip(Math.Max(0, conv.Messages.Count - conv.Unread)).Where(m => m.Highlight);
            foreach (var m in unseen)
                if (first == null || m.Time < first.Value.Message.Time) first = (conv, m);
        }
        if (first is not { } target) { StatusText.Text = "Nothing unread names you or answers you."; return; }

        SelectConversation(target.Conv);
        if (target.Message.Channel is { } channel && channel != target.Conv.CurrentChannel && RoomFor(target.Conv) is { } room2)
        {
            target.Conv.SelectChannel(channel);
            target.Conv.PendingChannel = null;
            RememberChannel(room2, channel);
            MarkShownRead(target.Conv);
            UpdateComposer();
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (target.Conv.Messages.Contains(target.Message)) MessageList.ScrollIntoView(target.Message);
        });
    }
}
