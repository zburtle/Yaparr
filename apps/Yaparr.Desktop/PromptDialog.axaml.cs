using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Yaparr.Desktop;

public partial class PromptDialog : Window
{
    public PromptDialog() => InitializeComponent();

    private bool _secret;

    private PromptDialog(string prompt, string initial, bool secret) : this()
    {
        PromptText.Text = prompt;
        Input.Text = initial;
        // A passphrase is hidden as it is typed, and kept exactly as typed (a name or a code has its spaces trimmed, a passphrase may not).
        _secret = secret;
        if (secret) Input.PasswordChar = '\u2022';
        Opened += (_, _) => { Input.Focus(); Input.SelectAll(); };
    }

    public static async Task<string?> AskAsync(Window? owner, string prompt, string initial = "", bool secret = false)
    {
        var dialog = new PromptDialog(prompt, initial, secret);
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
        Result = _secret ? Input.Text : Input.Text?.Trim();
        Close(Result);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
