using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Yaparr.AppCore;

namespace Yaparr.Desktop;

/// <summary>A device signed in to a backup server, with the recovery code that opens its backup.</summary>
public sealed record ServerRestoreLogin(Uri Server, string Username, string Token, string RecoveryCode);

// For the first-start screen, where there is no settings page yet: the server's address and the recovery code, then the sign-in in the browser.
public partial class ServerLoginDialog : Window
{
    private CancellationTokenSource? _waiting;

    public ServerLoginDialog() => InitializeComponent();

    public static async Task<ServerRestoreLogin?> AskAsync(Window? owner)
    {
        var dialog = new ServerLoginDialog();
        dialog.Opened += (_, _) => dialog.UrlBox.Focus();
        dialog.Closed += (_, _) => dialog._waiting?.Cancel();
        if (owner is { IsVisible: true })
            return await dialog.ShowDialog<ServerRestoreLogin?>(owner);

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var done = new TaskCompletionSource<ServerRestoreLogin?>();
        dialog.Closed += (_, _) => done.TrySetResult(dialog.Result);
        dialog.Show();
        return await done.Task;
    }

    private ServerRestoreLogin? Result { get; set; }

    private void Say(string text, bool problem = false)
    {
        Status.Text = text;
        Status.IsVisible = text.Length > 0;
        Status.Foreground = this.TryFindResource(problem ? "Bad" : "Muted", ActualThemeVariant, out var found) && found is IBrush brush ? brush : Brushes.Gray;
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var server = BackupServerClient.ParseServerAddress(UrlBox.Text);
        if (server == null) { Say("The server address is missing or cannot be used. It has to start with https:// (http:// only works for a server on this computer).", problem: true); return; }
        var code = RecoveryCode.Normalize(CodeBox.Text);
        if (code == null) { Say("That is not a recovery code. It is six groups of four letters and digits, like K7QD-M3XF-9PLR-2WTE-HV8N-C4YB.", problem: true); return; }

        OkButton.IsEnabled = UrlBox.IsEnabled = CodeBox.IsEnabled = false;
        _waiting = new CancellationTokenSource();
        try
        {
            using var signIn = new BrowserSignIn(server, Environment.MachineName);
            if (!await GetTopLevel(this)!.Launcher.LaunchUriAsync(signIn.LoginUrl))
            {
                Say("Couldn't open your browser.", problem: true);
                return;
            }
            Say("Waiting for you to sign in in your browser…");
            var (token, username) = await signIn.WaitAsync(cancel: _waiting.Token);
            Result = new ServerRestoreLogin(server, username, token, code);
            Close(Result);
        }
        catch (OperationCanceledException) { }
        catch (BackupServerException ex) { Say(ex.Message, problem: true); }
        finally { OkButton.IsEnabled = UrlBox.IsEnabled = CodeBox.IsEnabled = true; }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _waiting?.Cancel();
        Close(null);
    }
}
