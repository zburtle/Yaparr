using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

/// <summary>A person offered in the list that opens when you type @.</summary>
public sealed record MentionChoice(PeerId Account, string Name, bool Online)
{
    public string Label => Online ? Name : Name + "  (away)";
}

// Answering a message, and naming people with @.
public partial class MainWindow
{
    private MessageVm? _replyTo;
    private readonly List<Mention> _chosenMentions = new();
    private int _mentionStart = -1, _mentionDone = -1;

    private void SetUpReplies()
    {
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty) UpdateMentionList();
        };
        // Clicking the list takes the focus from the box for a moment; that must not close it before the click counts.
        InputBox.LostFocus += (_, _) => { if (!MentionList.IsPointerOver) CloseMentionList(); };
        MentionList.PointerReleased += (_, _) => { if (MentionList.SelectedItem != null) AcceptMention(); };
    }

    // ---- replying

    private void Reply_Click(object? sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is MessageVm { Key: not null, IsSystem: false } message) StartReply(message);
    }

    private void StartReply(MessageVm message)
    {
        _replyTo = message;
        ReplyBarTitle.Text = message.IsMine ? "Replying to yourself" : "Replying to " + message.Sender;
        ReplyBarText.Text = message.Preview;
        ReplyBar.IsVisible = true;
        InputBox.Focus();
    }

    private void CancelReply_Click(object? sender, RoutedEventArgs e) { ClearReply(); InputBox.Focus(); }

    private void ClearReply()
    {
        _replyTo = null;
        ReplyBar.IsVisible = false;
    }

    // Another conversation starts with a clean composer.
    private void ResetComposerExtras()
    {
        ClearReply();
        _chosenMentions.Clear();
        CloseMentionList();
    }

    // A click on the quote in a reply goes to the message it answers, when that is on show.
    private void Quote_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not MessageVm { ReplyKey: { } key } || _selected is not { } conv || conv.FindByKey(key) is not { } original) return;
        if (conv.Messages.Contains(original)) MessageList.ScrollIntoView(original);
    }

    // ---- naming people

    // The people offered while the words before the caret are an @ that starts a word, and what follows it so far.
    private void UpdateMentionList()
    {
        var text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        int at = caret == 0 ? -1 : text.LastIndexOf('@', caret - 1);
        if (SelectedRoom is not { } room || _node == null || at < 0 || (at > 0 && !char.IsWhiteSpace(text[at - 1])) || at == _mentionDone) { CloseMentionList(); return; }
        if (_mentionDone >= 0 && at != _mentionDone) _mentionDone = -1;
        var query = text[(at + 1)..caret];
        if (query.Length > 40 || query.Contains('\n')) { CloseMentionList(); return; }

        var me = _node.AccountId;
        var blocked = _settings.BlockedPeers.Select(b => b.Id).ToHashSet();
        var choices = room.People
            .Where(p => p.Account != me && !blocked.Contains(p.Account.Value) && !room.IsBanned(p.Account) && p.Name.Length > 0)
            .Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Online)
            .ThenByDescending(p => p.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .Select(p => new MentionChoice(p.Account, p.Name, p.Online))
            .ToList();
        if (choices.Count == 0) { CloseMentionList(); return; }

        _mentionStart = at;
        MentionList.ItemsSource = choices;
        MentionList.SelectedIndex = 0;
        MentionPopup.IsOpen = true;
    }

    private void CloseMentionList()
    {
        MentionPopup.IsOpen = false;
        _mentionStart = -1;
    }

    private void AcceptMention()
    {
        if (MentionList.SelectedItem is not MentionChoice choice || _mentionStart < 0) return;
        var text = InputBox.Text ?? "";
        int caret = Math.Clamp(InputBox.CaretIndex, _mentionStart, text.Length);
        var tag = "@" + choice.Name;
        _mentionDone = _mentionStart;
        InputBox.Text = text[.._mentionStart] + tag + " " + text[caret..];
        InputBox.CaretIndex = _mentionStart + tag.Length + 1;
        _chosenMentions.RemoveAll(m => m.Account == choice.Account);
        _chosenMentions.Add(new Mention(choice.Account, choice.Name));
        CloseMentionList();
        InputBox.Focus();
    }

    private void MentionList_DoubleTapped(object? sender, TappedEventArgs e) => AcceptMention();

    // The keys of the list while it is open, and Esc to give up a reply. True when the key was used.
    private bool HandleComposerKey(KeyEventArgs e)
    {
        if (MentionPopup.IsOpen)
        {
            int count = (MentionList.ItemsSource as IReadOnlyList<MentionChoice>)?.Count ?? 0;
            switch (e.Key)
            {
                case Key.Down when count > 0: MentionList.SelectedIndex = (MentionList.SelectedIndex + 1) % count; return true;
                case Key.Up when count > 0: MentionList.SelectedIndex = (MentionList.SelectedIndex - 1 + count) % count; return true;
                case Key.Enter or Key.Tab: AcceptMention(); return true;
                case Key.Escape: CloseMentionList(); return true;
            }
            return false;
        }
        if (e.Key == Key.Escape && _replyTo != null) { ClearReply(); return true; }
        return false;
    }

    // The people named in the words that are being sent: those picked from the list whose @name is still there.
    private List<Mention> ActiveMentions(string text) =>
        _chosenMentions.Where(m => text.Contains(m.Tag, StringComparison.OrdinalIgnoreCase)).ToList();
}
