using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.Chat;

namespace Yaparr.AndroidApp;

// Polls: asked from the + button's sheet, shown in the message as its question and answers, voted on by tapping an answer.
public sealed partial class MainView
{
    private readonly List<TextBox> _pollBoxes = new();
    private bool _pollWired;

    private void ShowPollForm()
    {
        if (_current == null || _session == null) return;
        if (!_pollWired)
        {
            _pollWired = true;
            PollQuestionBox.TextChanged += (_, _) => PollChanged();
            PollManyCheck.IsCheckedChanged += (_, _) => PollChanged();
        }
        PollQuestionBox.Text = "";
        PollManyCheck.IsChecked = false;
        PollAnswers.Children.Clear();
        _pollBoxes.Clear();
        for (int i = 0; i < Poll.MinOptions; i++) AddPollRow();
        PollPanel.IsVisible = true;
        PollQuestionBox.Focus();
    }

    private Poll? PollWritten() => Poll.Clean(PollQuestionBox.Text, _pollBoxes.Select(b => b.Text), PollManyCheck.IsChecked == true);

    private void PollChanged() => PollCreateButton.IsEnabled = PollWritten() != null;

    private void AddPollRow()
    {
        var box = new TextBox { PlaceholderText = "Answer " + (_pollBoxes.Count + 1), MaxLength = Poll.MaxOptionLength };
        box.TextChanged += (_, _) => PollChanged();
        _pollBoxes.Add(box);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        row.Children.Add(box);
        if (_pollBoxes.Count > Poll.MinOptions)
        {
            var remove = new Avalonia.Controls.Button { Content = "✕", Background = Brushes.Transparent, Padding = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Stretch };
            Grid.SetColumn(remove, 1);
            remove.Click += (_, _) =>
            {
                _pollBoxes.Remove(box);
                PollAnswers.Children.Remove(row);
                for (int i = 0; i < _pollBoxes.Count; i++) _pollBoxes[i].PlaceholderText = "Answer " + (i + 1);
                PollAddButton.IsVisible = true;
                PollChanged();
            };
            row.Children.Add(remove);
        }
        PollAnswers.Children.Add(row);
        PollAddButton.IsVisible = _pollBoxes.Count < Poll.MaxOptions;
        PollChanged();
    }

    private void PollAddClicked(object? sender, RoutedEventArgs e)
    {
        AddPollRow();
        _pollBoxes[^1].Focus();
    }

    private void PollCancelClicked(object? sender, RoutedEventArgs e) => PollPanel.IsVisible = false;

    private async void PollCreateClicked(object? sender, RoutedEventArgs e)
    {
        if (PollWritten() is not { } poll || _current is not { } chat || _session == null) return;
        PollPanel.IsVisible = false;
        var reply = _replyTo;
        ClearReply();
        try { await _session.SendPollAsync(chat, poll, reply?.Id); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ShowChatNote(chat, "Could not send the poll: " + ex.Message);
            if (reply != null) StartReply(reply);
        }
        ScrollToEnd();
    }

    // A tap on an answer votes for it, or takes the vote back. With one choice, voting for another answer moves the vote.
    private async void PollOptionTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is not PollOption option || option.Owner.Poll is not { } poll) return;
        var line = option.Owner;
        if (!option.Mine && !poll.Multiple)
            foreach (var other in line.PollOptions.Where(o => o.Mine).ToList())
                await ReactAsync(line, poll.KeycapOf(other.Index), on: false);
        await ReactAsync(line, poll.KeycapOf(option.Index), on: !option.Mine);
    }
}
