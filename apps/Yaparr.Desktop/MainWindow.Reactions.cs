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
    private void WireReactions(ConversationVm conv, Func<IReadOnlyCollection<string>, ReactionSet> source, Func<string, string, bool, string?, Task> send,
        Func<string, CustomEmoji, bool, string?, Task> sendCustom)
    {
        conv.ReactionSource = source;
        conv.React = send;
        conv.ReactCustom = sendCustom;
    }

    // The pictures of custom emoji used as reactions that are not on this computer yet are asked for once each (they are also fetched as a live reaction arrives,
    // this is for the ones read back from the history).
    private readonly HashSet<string> _reactionPicturesAsked = new();

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
        try { set = conv.ReactionSource(conv.AllMessages.Where(m => m.Key != null).Select(m => m.Key!).ToList()); }
        catch (Exception ex) when (ex is InvalidOperationException) { return; }
        var me = _node.AccountId;
        foreach (var vm in conv.AllMessages)
        {
            if (vm.Key == null) continue;
            var reactions = set.For(vm.Key);
            vm.SetReactions(reactions, me, account => AccountName(conv, account, me));
            foreach (var custom in reactions.Select(r => r.Custom).OfType<CustomEmoji>())
                if (vm.Source != null && !_node.HasAttachment(custom.Hash) && _reactionPicturesAsked.Add(custom.Hash))
                    _ = _node.FetchEmojiAsync(vm.Source, custom);
        }
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

    // A custom emoji picked in the picker: yours or the room's, as the reaction.
    private async Task ToggleReactionAsync(MessageVm message, CustomEmoji custom, bool on)
    {
        var conv = _conversations.FirstOrDefault(c => c.AllMessages.Contains(message));
        if (conv?.ReactCustom == null || message.Key == null) return;
        try
        {
            await conv.ReactCustom(message.Key, custom, on, message.Channel);
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

    private bool _pickingForComposer;

    // The button next to the message box: the same picker, but what is picked goes into what is being written.
    private void ComposerEmoji_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenComposerPicker(CustomTab.None);
    }

    private void OpenReactionPicker(MessageVm message, Control? near)
    {
        _pickingForComposer = false;
        ReactionPopup.Placement = PlacementMode.Pointer;
        _reactingTo = message;
        if (!_emojiWired) WireEmojiPicker();
        EmojiSearch.Text = "";
        ShowEmojiGroup(0);
        ReactionPopup.PlacementTarget = near ?? this;
        ReactionPopup.IsOpen = true;
        EmojiSearch.Focus();
    }

    // Every emoji, in the quick row, the grid and the category row alike, sits in a square of the same size, nine to a row, so they line up in columns.
    private const double EmojiCell = 38;
    private const double EmojiSize = 22;

    private Button EmojiButton(string emoji, string? name, double size = EmojiSize)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = emoji, FontFamily = EmojiFont, FontSize = size, LineHeight = size * 1.5, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ClipToBounds = false,
            Width = EmojiCell,
            Height = EmojiCell,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = emoji,
        };
        if (name != null) ToolTip.SetTip(button, name);
        button.Click += EmojiPicked_Click;
        return button;
    }

    // The grid scrolls, and a height that is not a whole number of rows shows the last row cut in half: so the height is a whole number of rows.
    private void SnapEmojiScroll()
    {
        const double room = 230;
        if (EmojiGrid.Children.Count == 0 || EmojiGrid.Children[0].Bounds.Height <= 0) return;
        double row = EmojiGrid.Children[0].Bounds.Height;
        double height = Math.Max(row, Math.Floor(room / row) * row);
        if (Math.Abs(EmojiScroll.MaxHeight - height) > 0.5) EmojiScroll.MaxHeight = height;
    }

    private void WireEmojiPicker()
    {
        _emojiWired = true;
        EmojiGrid.LayoutUpdated += (_, _) => SnapEmojiScroll();
        foreach (var emoji in EmojiData.Quick)
            QuickRow.Children.Add(EmojiButton(emoji, EmojiData.Find(emoji)?.Name));

        for (int i = 0; i < EmojiData.Groups.Count; i++)
        {
            int index = i;
            var tab = new Button
            {
                Content = new TextBlock { Text = GroupIcon(EmojiData.Groups[i].Name), FontFamily = EmojiFont, FontSize = 18, LineHeight = 27, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ClipToBounds = false,
                Width = EmojiCell,
                Height = EmojiCell,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(tab, EmojiData.Groups[i].Name);
            tab.Click += (_, _) => { EmojiSearch.Text = ""; ShowEmojiGroup(index); };
            EmojiTabs.Children.Add(tab);
        }

        EmojiSearch.TextChanged += (_, _) =>
        {
            if (_clearingEmojiSearch) return;
            var query = EmojiSearch.Text ?? "";
            if (query.Trim().Length == 0)
            {
                if (_customTab == CustomTab.None) ShowEmojiGroup(_emojiGroup);
                return;
            }
            _customTab = CustomTab.None;
            UpdateCustomTabs();
            var found = EmojiData.Search(query, 120);
            EmojiTitle.Text = found.Count == 0 ? "Nothing found" : found.Count + (found.Count == 1 ? " emoji" : " emoji");
            FillEmojiGrid(found);
        };
        ReactionPopup.Closed += (_, _) => { _reactingTo = null; _pickingForComposer = false; DetachCustomRoom(); };
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
        _customTab = CustomTab.None;
        DetachCustomRoom();
        UpdateCustomTabs();
        _emojiGroup = Math.Clamp(index, 0, EmojiData.Groups.Count - 1);
        var group = EmojiData.Groups[_emojiGroup];
        EmojiTitle.Text = group.Name;
        FillEmojiGrid(group.Entries);
    }

    private void FillEmojiGrid(IReadOnlyList<EmojiEntry> entries)
    {
        EmojiGrid.Children.Clear();
        foreach (var entry in entries) EmojiGrid.Children.Add(EmojiButton(entry.Emoji, entry.Name));
    }

    private void EmojiPicked_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string chosen && _pickingForComposer)
        {
            ReactionPopup.IsOpen = false;
            InsertIntoInput(chosen);
            return;
        }
        if ((sender as Control)?.Tag is not string emoji || _reactingTo is not { } message) return;
        ReactionPopup.IsOpen = false;
        // A custom emoji (its :name:, from the tabs of yours and the room's): the reaction carries its picture.
        if (emoji.Length > 2 && emoji[0] == ':' && emoji[^1] == ':')
        {
            var name = emoji[1..^1];
            if (_node == null || CustomBytes(name, SelectedRoom) is not { } bytes) return;
            CustomEmoji custom;
            try { custom = _node.PrepareEmoji(name, bytes); }
            catch (ArgumentException ex) { StatusText.Text = "Couldn't react: " + ex.Message; return; }
            var customKey = MessageEnvelope.CustomKey(custom);
            _ = ToggleReactionAsync(message, custom, on: !message.Reactions.Any(r => r.Emoji == customKey && r.Mine));
            return;
        }
        // Picking one you already have takes it off, as clicking it under the message does.
        bool has = message.Reactions.Any(r => r.Emoji == emoji && r.Mine);
        _ = ToggleReactionAsync(message, emoji, on: !has);
    }
}
