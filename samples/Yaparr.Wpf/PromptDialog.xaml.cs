using System.Windows;

namespace Yaparr.Wpf;

public partial class PromptDialog : Window
{
    private PromptDialog(string prompt, string initial)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        Input.Text = initial;
        Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public static string? Ask(Window? owner, string prompt, string initial = "")
    {
        var dialog = new PromptDialog(prompt, initial);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true ? dialog.Input.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
