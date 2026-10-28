using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yaparr.Desktop;

public partial class PromptDialog : Window
{
    public PromptDialog() => InitializeComponent();

    private PromptDialog(string prompt, string initial) : this()
    {
        PromptText.Text = prompt;
        Input.Text = initial;
        Opened += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public static async Task<string?> AskAsync(Window? owner, string prompt, string initial = "")
    {
        var dialog = new PromptDialog(prompt, initial);
        if (owner is { IsVisible: true })
            return await dialog.ShowDialog<string?>(owner);

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var done = new TaskCompletionSource<string?>();
        dialog.Closed += (_, _) => done.TrySetResult(dialog.Result);
        dialog.Show();
        return await done.Task;
    }

    private string? Result { get; set; }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Result = Input.Text?.Trim();
        Close(Result);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
