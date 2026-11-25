using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.AppCore;

namespace Yaparr.AndroidApp;

/// <summary>One row of the list of notifications.</summary>
public sealed class NotificationRow
{
    public NotificationRow(NotificationEntry entry)
    {
        Id = entry.Id;
        Heading = NotificationLog.Heading(entry);
        Body = entry.Body;
        ShowBody = entry.Body.Length > 0;
        TimeText = ListTime.Of(DateTimeOffset.FromUnixTimeMilliseconds(entry.Time), DateTimeOffset.Now);
        DotOpacity = entry.Read ? 0 : 1;
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
    /// <summary>The dot that marks one not seen yet: always there, so that the text lines up, and invisible once it is seen.</summary>
    public double DotOpacity { get; }
}

// The list of notifications (see NotificationLog): what would have been a notification while the app was not open, whether or not Android showed it.
public partial class MainView
{
    private readonly ObservableCollection<NotificationRow> _notificationRows = new();

    private void RefreshNotificationList()
    {
        if (_session is not { } session) return;
        var log = session.Notifications;
        _notificationRows.Clear();
        foreach (var entry in log.Items) _notificationRows.Add(new NotificationRow(entry));
        if (!ReferenceEquals(NotificationList.ItemsSource, _notificationRows)) NotificationList.ItemsSource = _notificationRows;
        int unread = log.UnreadCount;
        NotificationsBadge.IsVisible = unread > 0;
        NotificationsBadgeText.Text = unread > 99 ? "99+" : unread.ToString();
        NotificationsEmpty.IsVisible = _notificationRows.Count == 0;
        NotificationsMarkAll.IsEnabled = unread > 0;
        NotificationsClear.IsEnabled = _notificationRows.Count > 0;
    }

    private void NotificationsClicked(object? sender, RoutedEventArgs e)
    {
        RefreshNotificationList();
        ShowOnly(NotificationsPanel);
    }

    private void NotificationsBackClicked(object? sender, RoutedEventArgs e) => ShowOnly(ListPanel);

    private void NotificationsMarkAllClicked(object? sender, RoutedEventArgs e) => _session?.Notifications.MarkAllRead();

    private void NotificationsClearClicked(object? sender, RoutedEventArgs e) => _session?.Notifications.Clear();

    // A tap goes to the message: its chat, its text channel, and the message itself scrolled into view.
    private async void NotificationSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (NotificationList.SelectedItem is not NotificationRow row) return;
        NotificationList.SelectedItem = null;
        if (_session is not { } session) return;
        var entry = session.Notifications.Items.FirstOrDefault(n => n.Id == row.Id);
        if (entry == null) return;
        session.Notifications.MarkRead(entry.Id);
        if (session.FindChat(entry.ChatKey) is not { } chat) { ListStatus.Text = $"\"{entry.Title}\" is not in your list any more."; return; }
        OpenChat(chat);
        if (entry.Channel is { } channel && chat.IsRoom && channel != chat.CurrentChannel && chat.Channels.Any(c => c.Id == channel)) session.SelectChannel(chat, channel);
        // On to the message itself, asking the others for it if this phone no longer has it.
        if (entry.MessageKey is { Length: > 0 } key) await GoToMessageAsync(chat, key);
    }

    // The cross on a row takes that one notification out of the list.
    private void NotificationRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string id) _session?.Notifications.Remove(id);
        e.Handled = true;
    }
}
