using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Yaparr.Desktop;

/// <summary>Everyone you have blocked, with a button to unblock each. It is the only place for someone you knew only from a direct chat.</summary>
public static class BlockedDialog
{
    /// <summary>Shows the list. <paramref name="people"/> gives the current (id, name) pairs; <paramref name="unblock"/> unblocks one by id.</summary>
    public static async Task ShowAsync(Window owner, Func<IReadOnlyList<(string Id, string Name)>> people, Action<string> unblock)
    {
        var list = new StackPanel { Spacing = 6 };
        var dialog = new Window
        {
            Title = "Blocked people",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        void Fill()
        {
            list.Children.Clear();
            var blocked = people();
            if (blocked.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "You have not blocked anyone.", Opacity = 0.7 });
                return;
            }
            foreach (var (id, name) in blocked)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = name.Length > 0 ? name : "Unknown name", TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = id.Length > 12 ? id[..12] + "…" : id, FontSize = 11, Opacity = 0.6 });
                var button = new Button { Content = "Unblock", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
                var who = id;
                button.Click += (_, _) => { unblock(who); Fill(); };
                Grid.SetColumn(button, 1);
                row.Children.Add(text);
                row.Children.Add(button);
                list.Children.Add(row);
            }
        }

        var close = new Button { Content = "Close", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => dialog.Close();
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = "You do not see their messages or hear them. Unblocking lets them back into your lists.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        body.Children.Add(new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = new Border { Padding = new Thickness(0, 0, 12, 0), Child = list } });
        body.Children.Add(close);
        dialog.Content = body;
        Fill();
        await dialog.ShowDialog(owner);
    }
}
