using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.Chat;

namespace Yaparr.Desktop;

/// <summary>Asking for a poll: the question, two to ten answers, and whether more than one answer can be chosen.</summary>
public static class PollDialog
{
    /// <summary>The poll that was written, or null when it was cancelled.</summary>
    public static async Task<Poll?> AskAsync(Window owner)
    {
        Poll? result = null;
        var question = new TextBox { PlaceholderText = "Ask a question", MaxLength = Poll.MaxQuestionLength, AcceptsReturn = false };
        var rows = new StackPanel { Spacing = 6 };
        var boxes = new List<TextBox>();
        var add = new Button { Content = "Add an answer", HorizontalAlignment = HorizontalAlignment.Left };
        var many = new CheckBox { Content = "Allow more than one answer" };
        var create = new Button { Content = "Create poll", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = false, Classes = { "primary" } };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var dialog = new Window
        {
            Title = "Create a poll",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        Poll? Current() => Poll.Clean(question.Text, boxes.Select(b => b.Text), many.IsChecked == true);
        void Changed() => create.IsEnabled = Current() != null;
        void AddRow()
        {
            var box = new TextBox { PlaceholderText = "Answer " + (boxes.Count + 1), MaxLength = Poll.MaxOptionLength };
            box.TextChanged += (_, _) => Changed();
            boxes.Add(box);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(box);
            if (boxes.Count > Poll.MinOptions)
            {
                var remove = new Button { Content = "✕", Padding = new Thickness(8, 4), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(remove, "Remove this answer");
                Grid.SetColumn(remove, 1);
                remove.Click += (_, _) =>
                {
                    boxes.Remove(box);
                    rows.Children.Remove(row);
                    for (int i = 0; i < boxes.Count; i++) boxes[i].PlaceholderText = "Answer " + (i + 1);
                    add.IsVisible = true;
                    Changed();
                };
                row.Children.Add(remove);
            }
            rows.Children.Add(row);
            add.IsVisible = boxes.Count < Poll.MaxOptions;
            Changed();
            box.Focus();
        }
        question.TextChanged += (_, _) => Changed();
        many.IsCheckedChanged += (_, _) => Changed();
        add.Click += (_, _) => AddRow();
        for (int i = 0; i < Poll.MinOptions; i++) AddRow();

        void Create() { result = Current(); if (result != null) dialog.Close(); }
        create.Click += (_, _) => Create();
        cancel.Click += (_, _) => dialog.Close();
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) { dialog.Close(); e.Handled = true; }
            else if (e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control) { Create(); e.Handled = true; }
        };
        dialog.Opened += (_, _) => question.Focus();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                question, rows, add, many,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { create, cancel } },
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
