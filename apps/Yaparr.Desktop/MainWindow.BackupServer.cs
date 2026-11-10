using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Keeping a locked backup on a backup server (BackupServerClient), so it can be fetched from any computer. The person is given the server's
// address and an account by whoever runs it; they sign in in their browser, and the backup is locked here with a recovery code the app makes.
// The app works the same without any of it. The form is on the Backup page of the settings.
public partial class MainWindow
{
    private BackupServerState _server = new();
    private CancellationTokenSource? _serverSignIn;

    // Called when the Backup page is shown.
    private void FillServerForm()
    {
        _server = _data.LoadBackupServer();
        if (string.IsNullOrEmpty(ServerUrlBox.Text)) ServerUrlBox.Text = _server.Url;
        ShowServerState();
    }

    private void ShowServerState()
    {
        ServerSignedOutPanel.IsVisible = !_server.SignedIn;
        ServerSignedInPanel.IsVisible = _server.SignedIn;
        if (!_server.SignedIn) return;
        var host = Uri.TryCreate(_server.Url, UriKind.Absolute, out var uri) ? uri.Authority : _server.Url;
        ServerSignedInText.Text = $"Signed in to {host} as {_server.Username}." +
            (_server.Version > 0 ? $" The last backup from this computer is version {_server.Version}." : " No backup has been saved from this computer yet.");
        ShowRecoveryCodeButton.IsVisible = _server.RecoveryCode.Length > 0;
    }

    private void ServerStatusIs(string text, bool problem = false)
    {
        ServerStatus.Text = text;
        ServerStatus.IsVisible = text.Length > 0;
        ServerStatus.Foreground = this.TryFindResource(problem ? "Bad" : "Muted", ActualThemeVariant, out var found) && found is IBrush brush ? brush : Brushes.Gray;
    }

    // The server said this device is not signed in any more (signed out elsewhere, a changed password, a removed account): show the sign-in again.
    private void ServerFailed(BackupServerException ex)
    {
        if (ex.SignedOut)
        {
            _server.Token = "";
            _data.SaveBackupServer(_server);
            ServerUrlBox.Text = _server.Url;
            ShowServerState();
        }
        ServerStatusIs(ex.Message, problem: true);
    }

    private async void ServerSignIn_Click(object? sender, RoutedEventArgs e)
    {
        var server = BackupServerClient.ParseServerAddress(ServerUrlBox.Text);
        if (server == null)
        {
            ServerStatusIs("The server address is missing or cannot be used. It has to start with https:// (http:// only works for a server on this computer).", problem: true);
            return;
        }

        ServerSignInButton.IsEnabled = ServerUrlBox.IsEnabled = false;
        ServerCancelSignInButton.IsVisible = true;
        _serverSignIn = new CancellationTokenSource();
        try
        {
            using var signIn = new BrowserSignIn(server, Environment.MachineName);
            if (!await GetTopLevel(this)!.Launcher.LaunchUriAsync(signIn.LoginUrl))
            {
                ServerStatusIs("Couldn't open your browser.", problem: true);
                return;
            }
            ServerStatusIs("Waiting for you to sign in in your browser…");
            var (token, username) = await signIn.WaitAsync(cancel: _serverSignIn.Token);

            // Another server or another account than before: its backups have a recovery code of their own.
            var url = server.ToString().TrimEnd('/');
            if (url != _server.Url || username != _server.Username) _server = new BackupServerState();
            _server.Url = url;
            _server.Username = username;
            _server.Token = token;
            _data.SaveBackupServer(_server);
            _log.Write("signed in to a backup server");
            ShowServerState();
            ServerStatusIs("");
            Activate();
        }
        catch (OperationCanceledException) { ServerStatusIs(""); }
        catch (BackupServerException ex) { ServerStatusIs(ex.Message, problem: true); }
        finally
        {
            ServerSignInButton.IsEnabled = ServerUrlBox.IsEnabled = true;
            ServerCancelSignInButton.IsVisible = false;
        }
    }

    private void ServerCancelSignIn_Click(object? sender, RoutedEventArgs e) => _serverSignIn?.Cancel();

    private async void ServerSignOut_Click(object? sender, RoutedEventArgs e)
    {
        if (!_server.SignedIn) return;
        try
        {
            using var client = new BackupServerClient(new Uri(_server.Url), _server.Token);
            await client.SignOutAsync();
        }
        catch (BackupServerException) { }   // not reachable: this computer forgets the token all the same
        _server.Token = "";
        _data.SaveBackupServer(_server);
        ServerUrlBox.Text = _server.Url;
        ShowServerState();
        ServerStatusIs("Signed out. Your backups stay on the server.");
    }

    private async void ServerChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_server.Url, UriKind.Absolute, out var server))
            await GetTopLevel(this)!.Launcher.LaunchUriAsync(BackupServerClient.PasswordPage(server));
    }

    private async void ShowRecoveryCode_Click(object? sender, RoutedEventArgs e)
    {
        if (_server.RecoveryCode.Length > 0) await ShowRecoveryCodeAsync(_server.RecoveryCode, isNew: false);
    }

    // Shows the recovery code with a button to copy it. For a new code the person has to say they saved it (false if they back out).
    private async Task<bool> ShowRecoveryCodeAsync(string code, bool isNew)
    {
        bool saved = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window
        {
            Title = "Recovery code",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) =>
        {
            copy.Content = await Dialogs.CopyAsync(dialog, code) ? "Copied" : "Couldn't copy";
        };
        buttons.Children.Add(copy);
        if (isNew)
        {
            var done = new Button { Content = "I have saved it", IsDefault = true };
            done.Classes.Add("primary");
            done.Click += (_, _) => { saved = true; dialog.Close(); };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Click += (_, _) => dialog.Close();
            buttons.Children.Add(done);
            buttons.Children.Add(cancel);
        }
        else
        {
            var close = new Button { Content = "Close", IsDefault = true, IsCancel = true };
            close.Click += (_, _) => dialog.Close();
            buttons.Children.Add(close);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = isNew
                        ? "This is your recovery code. It locks your backups on this computer, so the server cannot read them. You need it to bring a backup back on a computer that has nothing, " +
                          "and nobody can recover it for you: not the server, and not your password.\n\nSave it now, in a password manager or on paper."
                        : "Your recovery code. It opens your backups, and you need it to restore on a computer that has nothing. Keep it somewhere safe and private.",
                },
                new TextBox { Text = code, IsReadOnly = true, FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 16, HorizontalContentAlignment = HorizontalAlignment.Center },
                buttons,
            },
        };
        await dialog.ShowDialog(this);
        return saved;
    }

    // The code for this device's backups, when it has none yet. With no backup on the server a new code is made. With one (made on another
    // device), the person types that code so one code opens them all, or chooses a new one; a typed code is checked against the backup, so a
    // slip cannot lead to backups locked with a code nobody has. Null if they back out.
    private async Task<string?> ChooseRecoveryCodeAsync(BackupServerClient client, int newest)
    {
        const string title = "Back up to a server";
        if (newest > 0)
        {
            var choice = await Dialogs.ChoiceAsync(this, title,
                "This account already has a backup on the server, made on another device. Type that device's recovery code so one code opens all your backups " +
                "(Settings > Backup > Show recovery code on that device).\n\nOr make a new code for this computer; the older backups will still need the old one.",
                "Type the code", "Make a new code", "Cancel");
            if (choice == "Type the code")
            {
                var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-check-" + Guid.NewGuid().ToString("N") + ".yapbak");
                try
                {
                    ServerStatusIs("Fetching the backup to check the code against…");
                    await client.DownloadAsync(temp);
                    ServerStatusIs("");
                    while (true)
                    {
                        var typed = await PromptDialog.AskAsync(this, "The recovery code of your other device:");
                        if (typed == null) return null;
                        var code = RecoveryCode.Normalize(typed);
                        if (code != null && await Task.Run(() => BackupServerClient.CodeOpens(temp, code))) return code;
                        await Dialogs.MessageAsync(this, title, "That code does not open the backup on the server. Check it and try again.");
                    }
                }
                finally { try { File.Delete(temp); } catch (IOException) { } }
            }
            if (choice != "Make a new code") return null;
        }
        var made = RecoveryCode.Generate();
        return await ShowRecoveryCodeAsync(made, isNew: true) ? made : null;
    }

    private async void BackupToServer_Click(object? sender, RoutedEventArgs e) => await BackupToServerAsync();

    private async Task BackupToServerAsync()
    {
        if (_node == null || !_server.SignedIn) return;
        BackupToServerButton.IsEnabled = ServerSignOutButton.IsEnabled = false;
        var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-backup-" + Guid.NewGuid().ToString("N") + ".yapbak");
        try
        {
            ServerStatusIs("Checking the server…");
            using var client = new BackupServerClient(new Uri(_server.Url), _server.Token);
            var versions = await client.VersionsAsync();
            var newest = versions.Count == 0 ? 0 : versions[0].Version;

            if (_server.RecoveryCode.Length == 0)
            {
                if (await ChooseRecoveryCodeAsync(client, newest) is not { } code) { ServerStatusIs("Backup not saved."); return; }
                _server.RecoveryCode = code;
                _data.SaveBackupServer(_server);
                ShowServerState();
            }
            // A newer backup than the one this computer last saved or restored came from another device; do not add to it without being told.
            else if (newest > 0 && newest != _server.Version
                && !await Dialogs.ConfirmAsync(this, "Back up to a server",
                    $"The server has a backup (version {newest}, {versions[0].At.LocalDateTime:g}) that this computer did not make or has not seen. Saving now adds a newer one; the older ones are kept for a while.",
                    "Save anyway", "Cancel")) { ServerStatusIs("Backup not saved."); return; }

            ServerStatusIs("Making the backup…");
            var recoveryCode = _server.RecoveryCode;
            await Task.Run(() => _data.ExportProfile(temp, recoveryCode));
            ServerStatusIs("Sending the backup…");
            _server.Version = await client.UploadAsync(temp, newest);
            _data.SaveBackupServer(_server);
            _log.Write($"backed up to a server (version {_server.Version})");
            ShowServerState();
            ServerStatusIs($"Backed up (version {_server.Version}). It is locked with your recovery code, so the server cannot read it.");
        }
        catch (BackupServerException ex) { ServerFailed(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ServerStatusIs("Backup failed: " + ex.Message, problem: true); }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            BackupToServerButton.IsEnabled = ServerSignOutButton.IsEnabled = true;
        }
    }

    // On the first-start screen of a computer with no account (there is no settings page yet): sign in, fetch the newest backup and bring it back.
    private async Task RestoreFromServerAsync()
    {
        const string title = "Restore from a server";
        if (await ServerLoginDialog.AskAsync(this) is not { } login) return;
        var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-restore-" + Guid.NewGuid().ToString("N") + ".yapbak");
        string staging;
        int version;
        try
        {
            using var client = new BackupServerClient(login.Server, login.Token);
            version = await client.DownloadAsync(temp);
            staging = await Task.Run(() => _data.StageProfile(temp, login.RecoveryCode));
        }
        catch (BackupServerException ex) { await Dialogs.MessageAsync(this, title, ex.Message); return; }
        catch (CryptographicException)
        {
            await Dialogs.MessageAsync(this, title, "That recovery code does not open the newest backup on the server. Check the code; if a newer backup was made with another code, use that one.");
            return;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, title, ex.Message);
            return;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }

        try { _data.ApplyStagedProfile(staging); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, title, "Restoring failed: " + ex.Message);
            return;
        }
        // This computer stays signed in and keeps the code, so its next backup needs nothing typed.
        _data.SaveBackupServer(new BackupServerState
        {
            Url = login.Server.ToString().TrimEnd('/'), Username = login.Username, Token = login.Token, RecoveryCode = login.RecoveryCode, Version = version,
        });
        _log.Write("restored a backup from a server");
        await Dialogs.MessageAsync(this, title, "Your account is back on this computer, as another device of it. Yaparr will restart now.");
        RestartApp();
    }
}
