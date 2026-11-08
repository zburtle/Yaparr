using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Emoji reactions: the emoji under a message with how many put them there, a click on one to add yours or take it back, and a picker with the quick
// ones and a search through all the emoji. The reactions themselves are messages that are not shown as such (see ReactionEvent in the library).
public partial class MainWindow
{
    // Emoji need a font that has them: the system's colour emoji font, wherever it is.
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji, Apple Color Emoji, Noto Color Emoji, Twemoji Mozilla, Segoe UI Symbol");

    private readonly Dictionary<ConversationVm, DispatcherTimer> _reactionTimers = new();

    /// <summary>Hooks a conversation up to the reactions of its room or direct chat.</summary>
    private void WireReactions(ConversationVm conv, Func<ReactionSet> source, Func<string, string, bool, string?, Task> send)
    {
        conv.ReactionSource = source;
        conv.React = send;
    }

    // Reactions come in bursts (everyone clicking the same emoji): they are put on the messages once for the lot.
    private void ScheduleReactions(ConversationVm conv)
    {
        if (_reactionTimers.ContainsKey(conv)) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _reactionTimers[conv] = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _reactionTimers.Remove(conv);
            RefreshReactions(conv);
        };
        timer.Start();
    }

    private void RefreshReactions(ConversationVm conv)
    {
        if (_node == null || conv.ReactionSource == null) return;
        ReactionSet set;
        try { set = conv.ReactionSource(); }
        catch (Exception ex) when (ex is InvalidOperationException) { return; }
        var me = _node.AccountId;
        foreach (var vm in conv.AllMessages)
            if (vm.Key != null) vm.SetReactions(set.For(vm.Key), me, account => AccountName(conv, account, me));
    }

    // A person's name for the list of who reacted: you, someone in this room or chat, someone met before, or the start of their ID.
    private string AccountName(ConversationVm conv, PeerId account, PeerId me)
    {
        if (account == me) return "You";
        if (RoomFor(conv) is { } room && room.Members.FirstOrDefault(p => p.Account == account) is { } member) return member.DisplayName;
        if (_settings.KnownPeers.FirstOrDefault(k => k.Id == account.Value)?.Name is { Length: > 0 } known) return known;
        return conv.Name is { Length: > 0 } name && conv.PeerId == account.Value ? name : account.Short;
    }

    // ---- chips ----

    private void ReactionChip_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not ReactionChipVm chip) return;
        if (chip.IsMore)
        {
            chip.Owner.ReactionsExpanded = !chip.Owner.ReactionsExpanded;
            chip.RaiseMoreText();
            if (sender is Visual v) ReactionStrip.Changed(v);
            return;
        }
        _ = ToggleReactionAsync(chip.Owner, chip.Emoji, on: !chip.Mine);
    }

    private async Task ToggleReactionAsync(MessageVm message, string emoji, bool on)
    {
        var conv = _conversations.FirstOrDefault(c => c.AllMessages.Contains(message));
        if (conv?.React == null || message.Key == null) return;
        try
        {
            await conv.React(message.Key, emoji, on, message.Channel);
            RefreshReactions(conv);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusText.Text = "Couldn't react: " + ex.Message;
        }
    }

    // ---- the picker ----

    private MessageVm? _reactingTo;
    private bool _emojiWired;
    private int _emojiGroup;

    // The small button that shows when the pointer is over a message.
    private void AddReactionHover_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is MessageVm { CanReact: true } message) OpenReactionPicker(message, sender as Control);
    }

    private void ReactItem_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageVm { Key: not null } message) OpenReactionPicker(message, MessageList);
    }

    private void OpenReactionPicker(MessageVm message, Control? near)
    {
        _reactingTo = message;
        if (!_emojiWired) WireEmojiPicker();
        EmojiSearch.Text = "";
        ShowEmojiGroup(0);
        ReactionPopup.PlacementTarget = near ?? this;
        ReactionPopup.IsOpen = true;
        EmojiSearch.Focus();
    }

    private Button EmojiButton(string emoji, string? name, double size)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = emoji, FontFamily = EmojiFont, FontSize = size },
            Padding = new Thickness(5, 3),
            Margin = new Thickness(1),
            Background = Brushes.Transparent,
            Tag = emoji,
        };
        if (name != null) ToolTip.SetTip(button, name);
        button.Click += EmojiPicked_Click;
        return button;
    }

    private void WireEmojiPicker()
    {
        _emojiWired = true;
        foreach (var emoji in EmojiData.Quick)
            QuickRow.Children.Add(EmojiButton(emoji, EmojiData.Find(emoji)?.Name, 22));

        for (int i = 0; i < EmojiData.Groups.Count; i++)
        {
            int index = i;
            var tab = new Button
            {
                Content = new TextBlock { Text = GroupIcon(EmojiData.Groups[i].Name), FontFamily = EmojiFont, FontSize = 16 },
                Padding = new Thickness(6, 2),
                Background = Brushes.Transparent,
            };
            ToolTip.SetTip(tab, EmojiData.Groups[i].Name);
            tab.Click += (_, _) => { EmojiSearch.Text = ""; ShowEmojiGroup(index); };
            EmojiTabs.Children.Add(tab);
        }

        EmojiSearch.TextChanged += (_, _) =>
        {
            var query = EmojiSearch.Text ?? "";
            if (query.Trim().Length == 0) { ShowEmojiGroup(_emojiGroup); return; }
            var found = EmojiData.Search(query, 120);
            EmojiTitle.Text = found.Count == 0 ? "Nothing found" : found.Count + (found.Count == 1 ? " emoji" : " emoji");
            FillEmojiGrid(found);
        };
        ReactionPopup.Closed += (_, _) => _reactingTo = null;
    }

    private static string GroupIcon(string group) => group switch
    {
        "Smileys & Emotion" => "\U0001F600",
        "People & Body" => "\U0001F44B",
        "Animals & Nature" => "\U0001F43B",
        "Food & Drink" => "\U0001F354",
        "Travel & Places" => "✈️",
        "Activities" => "⚽",
        "Objects" => "\U0001F4A1",
        "Symbols" => "\U0001F523",
        "Flags" => "\U0001F3C1",
        _ => "❓",
    };

    private void ShowEmojiGroup(int index)
    {
        _emojiGroup = Math.Clamp(index, 0, EmojiData.Groups.Count - 1);
        var group = EmojiData.Groups[_emojiGroup];
        EmojiTitle.Text = group.Name;
        FillEmojiGrid(group.Entries);
    }

    private void FillEmojiGrid(IReadOnlyList<EmojiEntry> entries)
    {
        EmojiGrid.Children.Clear();
        foreach (var entry in entries) EmojiGrid.Children.Add(EmojiButton(entry.Emoji, entry.Name, 20));
    }

    private void EmojiPicked_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string emoji || _reactingTo is not { } message) return;
        ReactionPopup.IsOpen = false;
        // Picking one you already have takes it off, as clicking it under the message does.
        bool has = message.Reactions.Any(r => r.Emoji == emoji && r.Mine);
        _ = ToggleReactionAsync(message, emoji, on: !has);
    }
}
