using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// Everything said in a room is kept, and a room opens with its newest messages: earlier ones are read, a page at a time, as the list is scrolled up to
// the top of what is shown.
public partial class MainWindow
{
    // How close to the top (in pixels) the list has to be scrolled for the next page to be read: about a screenful above the first message, so that it
    // is usually there before the top is reached.
    private const double EarlierThreshold = 600;

    private ScrollViewer? _messageScroll;
    private bool _loadingEarlierNow;

    private void WireMessageScroll()
    {
        MessageList.TemplateApplied += (_, e) =>
        {
            if (_messageScroll != null) _messageScroll.ScrollChanged -= MessageScroll_Changed;
            _messageScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_messageScroll != null) _messageScroll.ScrollChanged += MessageScroll_Changed;
        };
    }

    private void MessageScroll_Changed(object? sender, ScrollChangedEventArgs e) => MaybeLoadEarlier();

    /// <summary>Reads the next page of earlier messages when the top of the list is near (or when what is shown does not even fill it).</summary>
    private async void MaybeLoadEarlier()
    {
        if (_loadingEarlierNow || _messageScroll is not { } scroll || _selected is not { HasEarlier: true } conv) return;
        // A list that has not been laid out yet says nothing.
        if (scroll.Viewport.Height <= 0) return;
        if (scroll.Offset.Y > EarlierThreshold) return;
        _loadingEarlierNow = true;
        try
        {
            double offset = scroll.Offset.Y, extent = scroll.Extent.Height;
            int added = await conv.LoadEarlierAsync();
            if (added == 0 || _selected != conv) return;
            RefreshReactions(conv);
            // The new messages went in above, so what was on screen is lower by as much as the list grew: the view is moved by the same amount, and
            // again once the new items have been measured (the size of rows that were not shown is only known by then).
            Dispatcher.UIThread.Post(() => KeepPlace(scroll, offset, extent), DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(() => KeepPlace(scroll, offset, extent), DispatcherPriority.Background);
        }
        finally { _loadingEarlierNow = false; }
        // What was read may not have been enough to get past the threshold (short messages, a tall window): go on.
        Dispatcher.UIThread.Post(MaybeLoadEarlier, DispatcherPriority.Background);
    }

    private static void KeepPlace(ScrollViewer scroll, double offsetBefore, double extentBefore)
    {
        double grown = scroll.Extent.Height - extentBefore;
        if (grown > 0) scroll.Offset = new Vector(scroll.Offset.X, offsetBefore + grown);
    }

    // Reads a page for the conversation of a room, off the interface thread (it is a query on the database).
    private async Task<(IReadOnlyList<MessageVm> Messages, bool HasMore)> ReadEarlierAsync(ChatRoom room, string? channel, string beforeKey)
    {
        var page = await Task.Run(() => room.GetEarlierHistory(channel, beforeKey, 100));
        return (page.Messages.Select(ToVm).ToList(), page.HasMore);
    }
}
