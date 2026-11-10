using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>What was typed into <see cref="ServerLoginDialog"/>, checked: a usable address and username, and a long enough password.</summary>
public sealed record ServerLoginForm(Uri Server, string Username, string Password);

// One form for the server, the name and the password, for the first-start screen where there is no settings page yet.
public partial class ServerLoginDialog : Window
{
    public ServerLoginDialog() => InitializeComponent();

    public static async Task<ServerLoginForm?> AskAsync(Window? owner)
    {
        var dialog = new ServerLoginDialog();
        dialog.Opened += (_, _) => dialog.UrlBox.Focus();
        if (owner is { IsVisible: true })
            return await dialog.ShowDialog<ServerLoginForm?>(owner);

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var done = new TaskCompletionSource<ServerLoginForm?>();
        dialog.Closed += (_, _) => done.TrySetResult(dialog.Result);
        dialog.Show();
        return await done.Task;
    }

    private ServerLoginForm? Result { get; set; }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var form = BackupServerForm.Read(UrlBox.Text, UserBox.Text, PasswordBox.Text, out var problem);
        if (form == null)
        {
            Problem.Text = problem;
            Problem.IsVisible = true;
            return;
        }
        Result = form;
        Close(form);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}

/// <summary>Checks what was typed for a backup server, with a message that says what to fix.</summary>
public static class BackupServerForm
{
    public static ServerLoginForm? Read(string? address, string? username, string? password, out string problem)
    {
        problem = "";
        var server = BackupServerClient.ParseServerAddress(address);
        if (server == null) { problem = "The server address is missing or cannot be used. It has to start with https:// (http:// only works for a server on this computer)."; return null; }
        var name = BackupServerClient.NormalizeUsername(username);
        if (name == null) { problem = "A username is 3 to 32 letters, digits, dots, dashes or underscores."; return null; }
        if (BackupServerClient.CheckPassword(password) is { } why) { problem = why; return null; }
        return new ServerLoginForm(server, name, password!);
    }
}
