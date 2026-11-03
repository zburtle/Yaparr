using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.Updates;

namespace Yaparr.Desktop;

/// <summary>Says an update is available and lists what is in it in short lines under plain headings. The list scrolls when it is long.</summary>
public static class UpdateDialog
{
    /// <summary>True when the person chose to update now.</summary>
    public static async Task<bool> AskAsync(Window owner, Version version, Version current, string notes)
    {
        bool update = false;
        var list = new StackPanel { Spacing = 4 };
        foreach (var section in ReleaseNotes.Parse(notes))
        {
            if (section.Title.Length > 0)
                list.Children.Add(new TextBlock { Text = section.Title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 8, 0, 0) });
            foreach (var item in section.Items)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };
                row.Children.Add(new TextBlock { Text = "•", Opacity = 0.7 });
                var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                list.Children.Add(row);
            }
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window
        {
            Title = "Update available",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var body = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        body.Children.Add(new TextBlock { Text = $"Yaparr {version} is available", FontSize = 16, FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = $"You have {current}. Update now?", Opacity = 0.8 });
        if (list.Children.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "What's in it", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, -4) });
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Padding = new Thickness(0, 0, 12, 0), Child = list },
            });
        }
        body.Children.Add(buttons);
        dialog.Content = body;

        foreach (var (label, result) in new[] { ("Update now", true), ("Later", false) })
        {
            var button = new Button { Content = label, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += (_, _) => { update = result; dialog.Close(); };
            if (result) button.Classes.Add("primary");
            buttons.Children.Add(button);
        }
        await dialog.ShowDialog(owner);
        return update;
    }
}
