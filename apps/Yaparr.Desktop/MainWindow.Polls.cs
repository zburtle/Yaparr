using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.Chat;

namespace Yaparr.Desktop;

// Polls: asked from the + button's picker, shown in the message as its question and answers, voted on by clicking an answer (see Poll for how a vote travels).
public partial class MainWindow
{
    // The + button beside the message box: a small picker of what can be added, right above it.
    private void Plus_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PlusPopup.IsOpen = !PlusPopup.IsOpen;
    }

    // Choosing from the picker closes it (the choice's own click is handled first, as it is lower in the tree).
    private void WirePlusPicker()
    {
        PlusPopup.AddHandler(Button.ClickEvent, (_, _) => PlusPopup.IsOpen = false, handledEventsToo: true);
    }

    private async void Poll_Click(object? sender, RoutedEventArgs e)
    {
        PlusPopup.IsOpen = false;
        var conv = _selected;
        if (conv == null || _node == null) return;
        var poll = await PollDialog.AskAsync(this);
        if (poll == null || _selected != conv) return;
        try
        {
            var reply = _replyTo;
            var id = await conv.Send(MessageEnvelope.Wrap("", reply?.Key, null, poll: poll));
            ClearReply();
            var vm = new MessageVm(DateTimeOffset.Now, _node.DisplayName, poll.Words, isMine: true, id: id)
            {
                Channel = conv.IsRoom ? conv.CurrentChannel : null,
                Key = ChatMessage.KeyOf(_node.Id, id), ReplyKey = reply?.Key, Poll = poll,
            };
            conv.Add(vm);
            vm.SetReactions(Array.Empty<ReactionSummary>(), _node.AccountId, account => AccountName(conv, account, _node.AccountId));
            ScrollToEnd();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusText.Text = "Couldn't send the poll: " + ex.Message;
        }
    }

    // A click on an answer votes for it, or takes the vote back. With one choice, voting for another answer moves the vote.
    private async void PollOption_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not PollOptionVm option || option.Owner.Poll is not { } poll) return;
        var message = option.Owner;
        if (!option.Mine && !poll.Multiple)
            foreach (var other in message.PollOptions.Where(o => o.Mine).ToList())
                await ToggleReactionAsync(message, poll.KeycapOf(other.Index), on: false);
        await ToggleReactionAsync(message, poll.KeycapOf(option.Index), on: !option.Mine);
    }
}
