using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// How a room's channels are listed: in the categories the admins made (a header you can fold away), and, for one person's own sake, which text channels are pinned or
// muted and whether a group is in the room's order or the most recently active first. Plus the channel switcher (Ctrl+K) that jumps to any channel. The person's choices
// are in the settings and are the same on all of their devices (see ChannelPrefs); the categories are the room's, and only its admins change them.
public partial class MainWindow
{
    private void SaveChannelPrefs()
    {
        try { _data.SaveSettings(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Write("could not save the channel choices: " + ex.Message); }
    }

    private static bool InCategory(ChannelVm channel, string category) => string.Equals(channel.Category, category, StringComparison.CurrentCultureIgnoreCase);

    // Makes the room's list match the room and the person's choices: the flags on each channel, then the channels outside any category and each category with its own.
    private void ApplyChannelLayout(ConversationVm conv, ChatRoom room)
    {
        foreach (var c in conv.Channels.Where(c => c.IsText))
        {
            c.Pinned = ChannelPrefs.Has(_settings, room.RoomId, c.Id, ChannelFlag.Pinned);
            c.Muted = ChannelPrefs.Has(_settings, room.RoomId, c.Id, ChannelFlag.Muted);
        }
        List<ChannelVm> Text(string category) =>
            ChannelPrefs.Arrange(_settings, room.RoomId, conv.Channels.Where(c => c.IsText && InCategory(c, category)), c => c.Id, c => conv.ChannelLastActivity(c.Id));
        List<ChannelVm> Voice(string category) => conv.Channels.Where(c => c.IsVoice && InCategory(c, category)).ToList();
        var categories = room.Categories
            .Select(name => new ConversationVm.CategoryLayout(name, !ChannelPrefs.IsCollapsed(_settings, room.RoomId, name), Text(name).Concat(Voice(name)).ToList()))
            .ToList();
        conv.ApplyLayout(Text(""), Voice(""), categories);
    }

    // After another device changed a choice, or the settings were read: every room again.
    private void ApplyChannelLayoutEverywhere()
    {
        foreach (var (room, conv) in _roomConversations) ApplyChannelLayout(conv, room);
    }

    private void ChangeChannelFlag(ConversationVm conv, ChatRoom room, ChannelVm channel, ChannelFlag flag, bool on)
    {
        if (!ChannelPrefs.Set(_settings, room.RoomId, channel.Id, flag, on)) return;
        SaveChannelPrefs();
        ApplyChannelLayout(conv, room);
        if (flag == ChannelFlag.Muted) conv.RecountUnread();
    }

    private void ChangeRecentFirst(ConversationVm conv, ChatRoom room, bool on)
    {
        if (!ChannelPrefs.SetRecentFirst(_settings, room.RoomId, on)) return;
        SaveChannelPrefs();
        ApplyChannelLayout(conv, room);
    }

    // A message that is not news: it is in a channel the person muted and does not name them.
    private bool ChannelMuted(ChatRoom room, MessageVm message) =>
        message.Channel is { } channel && !ChannelPrefs.Counts(_settings, room.RoomId, channel, message.Highlight);

    // The menu of a text channel: the person's own choices for it.
    private void AddChannelChoices(List<Control> items, ConversationVm conv, ChatRoom room, ChannelVm channel)
    {
        items.Add(MenuEntry(channel.Pinned ? "Unpin from the top" : "Pin to the top", () => ChangeChannelFlag(conv, room, channel, ChannelFlag.Pinned, !channel.Pinned)));
        items.Add(MenuEntry(channel.Muted ? "Unmute channel" : "Mute channel", () => ChangeChannelFlag(conv, room, channel, ChannelFlag.Muted, !channel.Muted)));
        bool recent = ChannelPrefs.RecentFirst(_settings, room.RoomId);
        items.Add(MenuEntry(recent ? "List channels in the room's order" : "List most recent first", () => ChangeRecentFirst(conv, room, !recent)));
    }

    private static MenuItem MenuEntry(string header, Action act)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => act();
        return item;
    }

    // ---- categories ----

    private (ConversationVm Conv, ChatRoom Room)? RoomOf(CategoryVm category)
    {
        foreach (var (room, conv) in _roomConversations)
            if (conv.CategoryGroups.Contains(category)) return (conv, room);
        return null;
    }

    // A click on a category's header folds it away or opens it again.
    private void Category_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CategoryVm category } || RoomOf(category) is not var (conv, room)) return;
        if (!ChannelPrefs.SetCollapsed(_settings, room.RoomId, category.Name, category.Open)) return;
        SaveChannelPrefs();
        ApplyChannelLayout(conv, room);
    }

    // What an admin can do with a category: rename it, move it, take it away, or add a channel to it.
    private void Category_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Button { DataContext: CategoryVm category } button || RoomOf(category) is not var (_, room) || room.MyRole < RoomRole.Admin) return;
        var items = new List<Control>
        {
            MenuEntry("New text channel here", () => _ = NewChannelInCategoryAsync(room, ChannelKind.Text, category.Name)),
            MenuEntry("New voice channel here", () => _ = NewChannelInCategoryAsync(room, ChannelKind.Voice, category.Name)),
            new Separator(),
            MenuEntry("Rename category…", () => _ = RenameCategoryAsync(room, category)),
            MenuEntry("Move up", () => ChangeRoom(() => room.MoveCategory(category.Name, up: true))),
            MenuEntry("Move down", () => ChangeRoom(() => room.MoveCategory(category.Name, up: false))),
            MenuEntry("Remove category (the channels stay)", () => _ = DeleteCategoryAsync(room, category)),
        };
        new ContextMenu { ItemsSource = items }.Open(button);
        e.Handled = true;
    }

    // The part of a channel's admin menu that files it: out of any category, in one of the room's, or in a new one.
    private MenuItem CategoryMenuFor(ChatRoom room, ChannelVm channel)
    {
        var choices = new List<Control> { MenuEntry(channel.Category.Length == 0 ? "✓ No category" : "No category", () => ChangeRoom(() => room.SetChannelCategory(channel.Id, ""))) };
        foreach (var name in room.Categories)
            choices.Add(MenuEntry(InCategory(channel, name) ? "✓ " + name : name, () => ChangeRoom(() => room.SetChannelCategory(channel.Id, name))));
        choices.Add(new Separator());
        choices.Add(MenuEntry("New category…", () => _ = NewCategoryForAsync(room, channel)));
        return new MenuItem { Header = "Move to category", ItemsSource = choices };
    }

    // Runs a change to the room's channels; what an admin may not do (or what is wrong with it) shows in the status line.
    private void ChangeRoom(Action change)
    {
        try { change(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { StatusText.Text = ex.Message; }
    }

    private async Task NewCategoryForAsync(ChatRoom room, ChannelVm channel)
    {
        var name = await PromptDialog.AskAsync(this, "A name for the new category:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        ChangeRoom(() => room.SetChannelCategory(channel.Id, name));
    }

    private async Task NewChannelInCategoryAsync(ChatRoom room, ChannelKind kind, string category)
    {
        var name = await PromptDialog.AskAsync(this, kind == ChannelKind.Text ? "A name for the new text channel:" : "A name for the new voice channel:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        ChangeRoom(() => room.CreateChannel(name, kind, category));
    }

    private async Task RenameCategoryAsync(ChatRoom room, CategoryVm category)
    {
        var name = await PromptDialog.AskAsync(this, "A new name for the category:", category.Name);
        if (string.IsNullOrWhiteSpace(name) || name == category.Name) return;
        ChangeRoom(() => room.RenameCategory(category.Name, name));
    }

    private async Task DeleteCategoryAsync(ChatRoom room, CategoryVm category)
    {
        if (!await Dialogs.ConfirmAsync(this, "Remove category", $"Remove the category \"{category.Name}\" for everyone? Its channels stay, outside any category.", "Remove", "Keep")) return;
        ChangeRoom(() => room.DeleteCategory(category.Name));
    }

    // ---- the channel switcher ----

    // Opens a text channel of a room (in the room's own window), whichever room is on show.
    private void OpenTextChannel(ConversationVm conv, ChatRoom room, string channelId)
    {
        if (_selected != conv) SelectConversation(conv);
        conv.SelectChannel(channelId);
        conv.PendingChannel = null;
        RememberChannel(room, channelId);
        MarkShownRead(conv);
        ApplyChannelLayout(conv, room);
        UpdateComposer();
        ScrollToEnd();
        InputBox.Focus();
    }

    private void ChannelSwitcher_Click(object? sender, RoutedEventArgs e) => _ = ShowChannelSwitcherAsync();

    private async Task ShowChannelSwitcherAsync()
    {
        var items = new List<ChannelSwitcher.Item>();
        foreach (var (room, conv) in _roomConversations)
        {
            if (!conv.ShowChannels) continue;
            foreach (var c in conv.Channels)
            {
                string where = c.Category.Length > 0 ? conv.Title + " / " + c.Category : conv.Title;
                string notes = string.Join(", ", new[] { c.Pinned ? "pinned" : null, c.Muted ? "muted" : null }.OfType<string>());
                items.Add(new ChannelSwitcher.Item(conv, room, c, where, (c.IsText ? "# " : "\U0001F50A ") + c.Name, notes, c.IsText && !c.Muted ? c.Unread : 0,
                    c.IsText ? conv.ChannelLastActivity(c.Id) : 0, conv == _selected));
            }
        }
        if (items.Count == 0) return;
        var picked = await ChannelSwitcher.AskAsync(this, items);
        if (picked == null) return;
        if (picked.Channel.IsVoice)
        {
            if (_selected != picked.Conv) SelectConversation(picked.Conv);
            JoinVoiceIn(picked.Conv, picked.Room, picked.Channel.Id);
        }
        else OpenTextChannel(picked.Conv, picked.Room, picked.Channel.Id);
    }
}
