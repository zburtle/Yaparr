using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

/// <summary>The header of a category in the list of channels: tap it to fold the category away or open it.</summary>
public sealed class CategoryHeader
{
    public CategoryHeader(string name, bool open) { Name = name; Open = open; }

    public string Name { get; }
    public bool Open { get; }
    public string Label => (Open ? "▾ " : "▸ ") + Name.ToUpperInvariant();
}

// The list of all of a room's channels (the # button in front of the row of channels): grouped by the room's categories, which fold away; find a channel by typing, open
// it, and choose for yourself which to pin or mute. What is chosen is the same on all your devices (see ChannelPrefs).
public sealed partial class MainView
{
    private readonly ObservableCollection<object> _channelRows = new();
    private bool _channelListWired;

    private void ChannelListClicked(object? sender, RoutedEventArgs e)
    {
        if (_current is not { IsRoom: true } chat || _session == null) return;
        if (!_channelListWired)
        {
            _channelListWired = true;
            ChannelRows.ItemsSource = _channelRows;
            ChannelSearchBox.TextChanged += (_, _) => RefillChannelRows();
            ChannelRecentCheck.IsCheckedChanged += (_, _) =>
            {
                if (_current is { IsRoom: true } open && _session != null && ChannelRecentCheck.IsChecked != _session.RecentFirst(open)) _session.SetRecentFirst(open, ChannelRecentCheck.IsChecked == true);
            };
        }
        ChannelSearchBox.Text = "";
        ChannelRecentCheck.IsChecked = _session.RecentFirst(chat);
        RefillChannelRows();
        ChannelPanel.IsVisible = true;
    }

    // With words typed: the channels whose name (or category) has every word, in one list. Otherwise all of them under their categories, a folded category with its header only.
    private void RefillChannelRows()
    {
        if (_current is not { IsRoom: true } chat || _session == null) return;
        var words = (ChannelSearchBox.Text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _channelRows.Clear();
        if (words.Length > 0)
        {
            foreach (var chip in chat.EveryChannel.Where(c => words.All(w => (c.Name + " " + c.Category).ToLowerInvariant().Contains(w))).OrderByDescending(c => c.Unread && !c.Muted)) _channelRows.Add(chip);
            return;
        }
        string? shown = null;
        bool open = true;
        foreach (var chip in chat.Channels)
        {
            if (chip.Category.Length > 0 && !string.Equals(chip.Category, shown, StringComparison.CurrentCultureIgnoreCase))
            {
                shown = chip.Category;
                open = !_session.IsCategoryCollapsed(chat, chip.Category);
                _channelRows.Add(new CategoryHeader(chip.Category, open));
            }
            if (chip.Category.Length == 0) { shown = null; open = true; }
            if (open) _channelRows.Add(chip);
        }
    }

    private void ChannelCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (_current is not { IsRoom: true } chat || _session == null || (sender as Control)?.DataContext is not CategoryHeader header) return;
        _session.SetCategoryCollapsed(chat, header.Name, header.Open);
        RefillChannelRows();
    }

    private void ChannelRowClicked(object? sender, RoutedEventArgs e)
    {
        if (_current == null || (sender as Control)?.DataContext is not ChannelChip chip) return;
        ChannelPanel.IsVisible = false;
        _session?.SelectChannel(_current, chip.Id);
        ScrollToEnd();
    }

    private void ChannelPinClicked(object? sender, RoutedEventArgs e) => ChangeChannel(sender, ChannelFlag.Pinned, chip => !chip.Pinned);

    private void ChannelMuteClicked(object? sender, RoutedEventArgs e) => ChangeChannel(sender, ChannelFlag.Muted, chip => !chip.Muted);

    private void ChangeChannel(object? sender, ChannelFlag flag, Func<ChannelChip, bool> on)
    {
        if (_current is not { IsRoom: true } chat || _session == null || (sender as Control)?.DataContext is not ChannelChip chip) return;
        _session.SetChannelFlag(chat, chip.Id, flag, on(chip));
        if (flag == ChannelFlag.Pinned) RefillChannelRows();   // the pinned channel moves up in its group
    }

    private void ChannelPanelDone(object? sender, RoutedEventArgs e) => ChannelPanel.IsVisible = false;
}
