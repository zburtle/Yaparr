using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>One row of the list of notifications.</summary>
public sealed class NotificationItem
{
    public NotificationItem(NotificationEntry entry)
    {
        Id = entry.Id;
        Heading = NotificationLog.Heading(entry);
        Body = entry.Body;
        TimeText = ListTime.Of(DateTimeOffset.FromUnixTimeMilliseconds(entry.Time), DateTimeOffset.Now);
        IsUnread = !entry.Read;
        Mentioned = entry.Mentioned;
        ShowBody = entry.Body.Length > 0;
        CanAnswer = entry.IsOpenInvite;
        OutcomeText = entry.Outcome switch { NotificationOutcomes.Joined => "You joined", NotificationOutcomes.Declined => "Declined", _ => "" };
        ShowOutcome = OutcomeText.Length > 0;
    }

    /// <summary>An invitation nobody has answered: it has Join and Decline.</summary>
    public bool CanAnswer { get; }
    public string OutcomeText { get; }
    public bool ShowOutcome { get; }

    public string Id { get; }
    public string Heading { get; }
    public string Body { get; }
    public bool ShowBody { get; }
    public string TimeText { get; }
    public bool IsUnread { get; }
    /// <summary>The dot that marks an unseen one: drawn, but invisible once seen, so that the text stays in line.</summary>
    public double DotOpacity => IsUnread ? 1 : 0;
    public bool Mentioned { get; }
}

// The list of notifications: every message that was worth one while it was not in front of you, with the ones not seen yet marked. Opening a chat marks its entries seen.
public partial class MainWindow
{
    private NotificationLog _notificationLog = new();
    private readonly ObservableCollection<NotificationItem> _notificationItems = new();
    private object? _notificationsOpener;

    private void SetUpNotificationList()
    {
        _notificationLog = new NotificationLog(_data.LoadNotifications());
        NotificationList.ItemsSource = _notificationItems;
        _notificationLog.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshNotificationList);
        RefreshNotificationList();
    }

    // The same text everywhere that says how many are unseen.
    private void RefreshNotificationList()
    {
        _notificationItems.Clear();
        foreach (var entry in _notificationLog.Items) _notificationItems.Add(new NotificationItem(entry));
        int unread = _notificationLog.UnreadCount;
        NotificationsBadge.IsVisible = unread > 0;
        NotificationsBadgeText.Text = unread > 99 ? "99+" : unread.ToString();
        NotificationsEmpty.IsVisible = _notificationItems.Count == 0;
        NotificationsMarkAll.IsEnabled = unread > 0;
        NotificationsClear.IsEnabled = _notificationItems.Count > 0;
        SaveNotificationList();
    }

    private void SaveNotificationList()
    {
        if (!_notificationLog.Dirty) return;
        try { _data.SaveNotifications(_notificationLog.ForSaving); _notificationLog.Saved(); }
        catch (IOException ex) { _log.Write("Could not save the notification list: " + ex.Message); }
    }

    /// <summary>The key of a chat in the list: a room's id, or the other person's id.</summary>
    private string NotificationKeyOf(ConversationVm conv) => RoomFor(conv)?.RoomId ?? conv.PeerId ?? "";

    // A message that was worth a notification and was not in front of you: written down, so that it can be found again whether or not the system showed it.
    private void RecordNotification(ConversationVm conv, MessageVm message)
    {
        var key = NotificationKeyOf(conv);
        if (key.Length == 0) return;
        var body = message.HasFile ? "Sent a file: " + message.FileName : NotificationPolicy.Body(message.Text, message.HasAttachment, isGif: false);
        _notificationLog.Add(new NotificationEntry
        {
            Time = message.Time.ToUnixTimeMilliseconds(),
            ChatKey = key,
            IsRoom = conv.IsRoom,
            Title = conv.Title,
            Sender = message.Sender,
            Body = body.Length > 0 ? body : "New message",
            Channel = conv.IsRoom ? message.Channel : null,
            MessageKey = message.Key,
            Mentioned = message.Highlight,
        });
    }

    // A chat on show has been seen, in the list of notifications too.
    private void MarkNotificationsRead(ConversationVm? conv)
    {
        if (conv == null || !IsActive) return;
        var key = NotificationKeyOf(conv);
        if (key.Length > 0) _notificationLog.MarkChatRead(key);
    }

    private void Notifications_Click(object? sender, RoutedEventArgs e) => ShowNotificationList();

    private void ShowNotificationList()
    {
        if (NotificationsOverlay.IsVisible) return;
        _notificationsOpener = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        RefreshNotificationList();
        NotificationsOverlay.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => NotificationsOverlay.Focus());
    }

    private void CloseNotificationList()
    {
        if (!NotificationsOverlay.IsVisible) return;
        NotificationsOverlay.IsVisible = false;
        (_notificationsOpener as Control)?.Focus();
        _notificationsOpener = null;
    }

    private void CloseNotifications_Click(object? sender, RoutedEventArgs e) => CloseNotificationList();

    private void NotificationsOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == NotificationsOverlay) CloseNotificationList();
    }

    private void NotificationsOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseNotificationList();
        e.Handled = true;
    }

    private void NotificationsMarkAll_Click(object? sender, RoutedEventArgs e) => _notificationLog.MarkAllRead();

    private void NotificationsClear_Click(object? sender, RoutedEventArgs e) => _notificationLog.Clear();

    private void NotificationRemove_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string id) _notificationLog.Remove(id);
        e.Handled = true;
    }

    // Opening a notification goes to the message: its chat, its text channel, and the message itself scrolled into view. The list is put away.
    private async void NotificationList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NotificationList.SelectedItem is not NotificationItem item) return;
        NotificationList.SelectedItem = null;
        var entry = _notificationLog.Items.FirstOrDefault(n => n.Id == item.Id);
        if (entry == null) return;
        var conv = _conversations.FirstOrDefault(c => NotificationKeyOf(c) == entry.ChatKey);
        _notificationLog.MarkRead(entry.Id);
        if (conv == null) { StatusText.Text = $"\"{entry.Title}\" is not in your list any more."; return; }
        CloseNotificationList();
        ShowFromTray();
        SelectConversation(conv);
        if (entry.Channel is { } channel) ShowChannel(conv, channel);
        // On to the message itself, asking the others for it if this computer no longer has it.
        if (entry.MessageKey is { Length: > 0 } key) await GoToMessageAsync(conv, key);
    }
}
