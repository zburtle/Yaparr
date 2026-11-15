using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>Which saved copy of the backup to restore, when the server holds more than one.</summary>
public static class VersionPickerDialog
{
    /// <summary>
    /// Shows the copies on the server, newest first, with <paramref name="selected"/> chosen, and returns the version the person picked (null if they
    /// cancelled). <paramref name="problem"/> is shown above the list, for when the copy tried before could not be opened.
    /// </summary>
    public static async Task<int?> AskAsync(Window owner, IReadOnlyList<ServerBackupVersion> versions, int selected, string? problem = null)
    {
        var choices = ServerRestoreChoices.Describe(versions);
        int? result = null;

        var list = new ListBox
        {
            ItemsSource = choices.Select(c => c.Label).ToList(),
            SelectedIndex = Math.Max(0, choices.ToList().FindIndex(c => c.Version == selected)),
            SelectionMode = SelectionMode.Single,
            MaxHeight = 260,
        };
        var intro = new TextBlock
        {
            Text = "Choose the backup to bring back. The newest is your account as it was last saved; pick an older one only if the newest is wrong or cannot be opened.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var restore = new Button { Content = "Restore this version", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        restore.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(restore);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        if (!string.IsNullOrEmpty(problem)) panel.Children.Add(new TextBlock { Text = problem, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        panel.Children.Add(intro);
        panel.Children.Add(list);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Restore from a server",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        void Accept()
        {
            if (list.SelectedIndex < 0) return;
            result = choices[list.SelectedIndex].Version;
            dialog.Close();
        }
        restore.Click += (_, _) => Accept();
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => Accept();
        dialog.Opened += (_, _) => list.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }
}
