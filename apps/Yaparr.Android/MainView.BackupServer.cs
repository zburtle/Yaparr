using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.AndroidApp;

// Keeping a locked backup on a backup server (BackupServerClient), so it can be fetched on any phone or computer. The person is given the server's address and
// an account on it by whoever runs it; they sign in in the phone's browser, and the backup is locked here with a Sync key the app makes and shows once.
// It is the Backup page of the computer app on a phone, and it works the same without any of it. The sign-in comes back to a port of this phone (BrowserSignIn),
// so the app has to be alive while the browser is in front: signing in takes a minute at most.
public partial class MainView
{
    private BackupServerState _server = new();
    private CancellationTokenSource? _serverSignIn, _serverRestore;
    private Action? _autoChanged;

    // Shown beside the address in the browser's sign-in page and in the server's list of devices.
    private static string DeviceName() => $"{Android.OS.Build.Manufacturer} {Android.OS.Build.Model}".Trim();

    // Called when the settings open.
    private void FillServerForm()
    {
        _server = _data.LoadBackupServer();
        ServerUrlBox.Text = ReleaseConfig.Current.BackupServerToOffer(_server.Url);
        ShowServerState();
        ServerNote("");
        // What an automatic backup did (made one, paused, failed) shows here when it happens.
        if (_autoChanged != null) AppHost.Auto.Changed -= _autoChanged;
        _autoChanged = () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { _server = _data.LoadBackupServer(); ShowServerState(); });
        AppHost.Auto.Changed += _autoChanged;
    }

    private void GatewayClicked(object? sender, RoutedEventArgs e)
    {
        _settings.GatewayWhenUdpBlocked = GatewayCheck.IsChecked == true;
        _data.SaveSettings(_settings);
        _session?.SetGatewayAllowed(_settings.GatewayWhenUdpBlocked);
    }

    private void LeaveForAbsentClicked(object? sender, RoutedEventArgs e)
    {
        _settings.LeaveForAbsent = LeaveForAbsentCheck.IsChecked == true;
        _data.SaveSettings(_settings);
        if (_session != null) _session.SetLeaveForAbsent(_settings.LeaveForAbsent);
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")} ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
        return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
    }

    private void ShowServerState()
    {
        ServerSignedOutPanel.IsVisible = !_server.SignedIn;
        ServerSignedInPanel.IsVisible = _server.SignedIn;
        LeaveForAbsentCheck.IsChecked = _settings.LeaveForAbsent;
        GatewayCheck.IsChecked = _settings.GatewayWhenUdpBlocked;
        if (!_server.SignedIn) return;
        var host = Uri.TryCreate(_server.Url, UriKind.Absolute, out var uri) ? uri.Authority : _server.Url;
        ServerSignedInText.Text = $"Signed in to {host} as {_server.Username}.";
        ShowRecoveryCodeButton.IsVisible = ReplaceSyncKeyButton.IsVisible = _server.RecoveryCode.Length > 0;

        var trouble = _server.Failures > 0 || _server.AutoNote.StartsWith("Sync stopped", StringComparison.Ordinal) || _server.AutoNote == AccountCloudSync.KeyChangedNote;
        AutoBackupStatus.Text = trouble ? _server.AutoNote
            : _server.RecoveryCode.Length == 0 || _server.Version <= 0 ? "Syncing starts once your Sync key has been made. Tap Sync now."
            : _server.LastBackup is { } last ? $"Up to date. Last synced {Ago(last)}."
            : "Syncing by itself.";
        AutoBackupStatus.IsVisible = AutoBackupStatus.Text.Length > 0;
    }

    private void ServerNote(string text, bool problem = false)
    {
        ServerStatus.Text = text;
        ServerStatus.IsVisible = text.Length > 0;
        ServerStatus.Foreground = Yaparr.AndroidApp.Theme.Brush(problem ? "Bad" : "Muted");
    }

    // Keeps a sign-in this phone has just been given. Another server or another account than before: its backups have a Sync key of their own.
    private void AdoptSignIn(Uri server, string token, string username)
    {
        var url = server.ToString().TrimEnd('/');
        _server = _data.LoadBackupServer();
        if (url != _server.Url || username != _server.Username) _server = new BackupServerState();
        _server.Url = url;
        _server.Username = username;
        _server.Token = token;
        _data.SaveBackupServer(_server);
        AppHost.Session?.MailboxNow();   // starts leaving and fetching what rooms have in their mailboxes
    }

    // The server said this phone is not signed in any more (signed out elsewhere, a changed password, a removed account): the sign-in shows again.
    private void ServerFailed(BackupServerException ex)
    {
        if (ex.SignedOut)
        {
            _server.Token = "";
            _data.SaveBackupServer(_server);
            ServerUrlBox.Text = ReleaseConfig.Current.BackupServerToOffer(_server.Url);
            ShowServerState();
        }
        ServerNote(ex.Message, problem: true);
    }

    // ---- signing in ----

    private async Task<bool> OpenInBrowserAsync(Uri uri) =>
        TopLevel.GetTopLevel(this)?.Launcher is { } launcher && await launcher.LaunchUriAsync(uri);

    private async void ServerSignInClicked(object? sender, RoutedEventArgs e)
    {
        var server = BackupServerClient.ParseServerAddress(ServerUrlBox.Text);
        if (server == null)
        {
            ServerNote("The server address is missing or cannot be used. It has to start with https://.", problem: true);
            return;
        }

        ServerSignInButton.IsEnabled = ServerUrlBox.IsEnabled = false;
        ServerCancelSignInButton.IsVisible = true;
        _serverSignIn = new CancellationTokenSource();
        try
        {
            using var signIn = new BrowserSignIn(server, DeviceName(), throughAppLink: true);
            if (!await OpenInBrowserAsync(signIn.LoginUrl)) { ServerNote("Couldn't open your browser.", problem: true); return; }
            ServerNote("Waiting for you to sign in in your browser. When it says you are signed in, come back to Yaparr.");
            var (token, username) = await signIn.WaitAsync(cancel: _serverSignIn.Token);
            AdoptSignIn(server, token, username);
            ShowServerState();
            ServerNote("");
            await OfferNotificationsAsync();      // as soon as the phone is signed in: notifications and push, once
            await StartFirstBackupAsync();
        }
        catch (OperationCanceledException) { ServerNote(""); }
        catch (BackupServerException ex) { ServerNote(ex.Message, problem: true); }
        finally
        {
            ServerSignInButton.IsEnabled = ServerUrlBox.IsEnabled = true;
            ServerCancelSignInButton.IsVisible = false;
        }
    }

    // Right after signing in to an account that has no backup yet, the first one starts by itself: it makes the Sync key (shown for the person to keep),
    // which automatic backups and the sync of name, rooms and blocks both need. If the server already has a backup, which another device made, nothing is
    // saved without the person choosing (Back up now asks which code opens it).
    private async Task StartFirstBackupAsync()
    {
        if (!_server.SignedIn || _server.RecoveryCode.Length > 0) return;
        try
        {
            using var client = new BackupServerClient(new Uri(_server.Url), _server.Token);
            if ((await client.VersionsAsync()).Count > 0) return;
        }
        catch (BackupServerException ex) { ServerFailed(ex); return; }
        BackupToServerClicked(null, new RoutedEventArgs());
    }

    // Signed in and never backed up (it was signed in before this, or the first backup after sign-in did not happen): the first backup starts, which shows the
    // Sync key. It cannot be closed before the code is copied, so it comes back at the next start only if the app was closed in between.
    private async Task FirstBackupAtLaunchAsync()
    {
        var state = _data.LoadBackupServer();
        if (!state.SignedIn || state.RecoveryCode.Length > 0 || state.Version > 0) return;
        _server = state;
        await StartFirstBackupAsync();
    }

    private void ServerCancelSignInClicked(object? sender, RoutedEventArgs e) => _serverSignIn?.Cancel();

    private async void ServerSignOutClicked(object? sender, RoutedEventArgs e)
    {
        if (!_server.SignedIn) return;
        try
        {
            using var client = new BackupServerClient(new Uri(_server.Url), _server.Token);
            await client.SignOutAsync();
        }
        catch (BackupServerException) { }    // not reachable: this phone forgets the token all the same
        _server.Token = "";
        _data.SaveBackupServer(_server);
        ServerUrlBox.Text = ReleaseConfig.Current.BackupServerToOffer(_server.Url);
        ShowServerState();
        ServerNote("Signed out. What Yaparr Cloud holds stays there.");
    }

    // The password is changed on the server's own page, in the browser.
    private async void ServerChangePasswordClicked(object? sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_server.Url, UriKind.Absolute, out var server)) await OpenInBrowserAsync(BackupServerClient.PasswordPage(server));
    }

    // The account, with its backup, synced settings and wake-up registrations, is deleted on the server's own page, in the browser.
    private async void ServerDeleteAccountClicked(object? sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_server.Url, UriKind.Absolute, out var server)) await OpenInBrowserAsync(BackupServerClient.DeleteAccountPage(server));
    }

    // ---- small helpers that wait for an answer ----

    private Task<bool> ConfirmAsync(string text, string yes)
    {
        var answer = new TaskCompletionSource<bool>();
        Ask(text, yes, () => { answer.TrySetResult(true); return Task.CompletedTask; }, () => answer.TrySetResult(false));
        return answer.Task;
    }

    // The text typed; "" if the alternative button was chosen; null if the person backed out.
    private Task<string?> AskTextAsync(string title, string? alternative = null, int maxLength = 40)
    {
        var answer = new TaskCompletionSource<string?>();
        AskText(title, "", text => answer.TrySetResult(text), declined: () => answer.TrySetResult(null), alternative, chosen: () => answer.TrySetResult(""), maxLength);
        return answer.Task;
    }

    // ---- the Sync key ----

    private TaskCompletionSource<bool>? _codeAnswer;
    private bool _codeMustBeCopied;

    // Shows the Sync key with a button to copy it. For a new code the person has to say they saved it (false if they back out).
    private Task<bool> ShowRecoveryCodeAsync(string code, bool isNew)
    {
        CodeText.Text = isNew
            ? "This is your Sync key. It locks everything Yaparr Cloud holds for you, so the server cannot read it. You need it to set up a phone or computer that has nothing, " +
              "and nobody can recover it for you: not the server, and not your password.\n\nSave it now, in a password manager or on paper."
            : "Your Sync key. It locks what Yaparr Cloud holds for you, and you need it to set up a phone or computer that has nothing. Keep it somewhere safe and private.";
        CodeShown.Text = code;
        CodeCopyButton.Content = "Copy";
        CodeDoneButton.Content = isNew ? "I have saved it" : "Close";
        // A new code cannot be dismissed before it has been copied (or copying failed, and it has to be written down): there is no Cancel, and Back does nothing.
        _codeMustBeCopied = isNew;
        CodeDoneButton.IsEnabled = !isNew;
        CodeCancelButton.IsVisible = false;
        CodePanel.IsVisible = true;
        _codeAnswer = new TaskCompletionSource<bool>();
        return _codeAnswer.Task;
    }

    private async void CodeCopyClicked(object? sender, RoutedEventArgs e)
    {
        _codeMustBeCopied = false;
        CodeDoneButton.IsEnabled = true;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) { CodeCopyButton.Content = "Couldn't copy: write it down"; return; }
        await clipboard.SetTextAsync(CodeShown.Text ?? "");
        CodeCopyButton.Content = "Copied";
    }

    private void CodeDoneClicked(object? sender, RoutedEventArgs e)
    {
        CodePanel.IsVisible = false;
        _codeAnswer?.TrySetResult(true);
    }

    private void CodeCancelClicked(object? sender, RoutedEventArgs e)
    {
        CodePanel.IsVisible = false;
        _codeAnswer?.TrySetResult(false);
    }

    // A new Sync key locks everything the account keeps in sync again and removes what the old one locked; the person's other devices then need the new key.
    // Or, on a device that was left on the old key, the key made elsewhere is entered.
    private async void ReplaceSyncKeyClicked(object? sender, RoutedEventArgs e)
    {
        if (!_server.SignedIn || _server.RecoveryCode.Length == 0) return;
        var entering = await ConfirmAsync("Did you make a new Sync key on another device? Then enter it here.\n\nIf not, choose Make a new key on the next screen: everything you keep in sync is locked again with it, what the old key locked is removed from Yaparr Cloud, and your other devices stop syncing until you enter the new key on each.", "Enter it");
        if (entering)
        {
            var typed = await AskTextAsync("The new Sync key:", maxLength: 200);
            if (typed == null) return;
            await AppHost.Auto.Gate.WaitAsync();
            try
            {
                var problem = await SyncKeyReplacement.AdoptAsync(_data, typed);
                _server = _data.LoadBackupServer();
                ShowServerState();
                ServerNote(problem.Length == 0 ? "Done. Syncing carries on with the new key." : problem, problem.Length > 0);
            }
            finally { AppHost.Auto.Gate.Release(); }
            return;
        }
        if (!await ConfirmAsync("Make a new Sync key for this account?", "Make a new key")) return;
        var made = RecoveryCode.Generate();
        if (!await ShowRecoveryCodeAsync(made, isNew: true)) return;

        BackupToServerButton.IsEnabled = ServerSignOutButton.IsEnabled = ReplaceSyncKeyButton.IsEnabled = false;
        ServerNote("Waiting for a sync that is running…");
        await AppHost.Auto.Gate.WaitAsync();
        try
        {
            var node = AppHost.Session?.Node;
            if (node == null) { ServerNote("Yaparr is not running yet.", problem: true); return; }
            ServerNote("Locking everything with the new key…");
            var result = await Task.Run(() => SyncKeyReplacement.ReplaceAsync(_data, node, made));
            _server = _data.LoadBackupServer();
            ShowServerState();
            ServerNote(result.Done ? (result.Problem.Length == 0 ? "Done. Enter the new key on your other devices." : result.Problem) : result.Problem, problem: !result.Done || result.Problem.Length > 0);
        }
        catch (BackupServerException ex) { ServerNote(ex.Message, problem: true); }
        finally
        {
            AppHost.Auto.Gate.Release();
            BackupToServerButton.IsEnabled = ServerSignOutButton.IsEnabled = ReplaceSyncKeyButton.IsEnabled = true;
        }
    }

    private void ShowRecoveryCodeClicked(object? sender, RoutedEventArgs e)
    {
        if (_server.RecoveryCode.Length > 0) _ = ShowRecoveryCodeAsync(_server.RecoveryCode, isNew: false);
    }

    // The code for this phone's backups, when it has none yet. With no backup on the server a new code is made. With one (made on another device), the person
    // types that code so one code opens them all, or chooses a new one; a typed code is checked against the backup, so a slip cannot lead to backups
    // locked with a code nobody has. Null if they back out.
    private async Task<string?> ChooseRecoveryCodeAsync(BackupServerClient client, int newest)
    {
        if (newest > 0)
        {
            var intro = "This account already has data on Yaparr Cloud, from another device. Type that device's Sync key (Settings > Yaparr Cloud > Show Sync key there) so all your devices use the same one. " +
                        "If you have lost it, you can start a new one here, but what is already stored cannot be opened with it.";
            var temp = Path.Combine(Path.GetTempPath(), "yaparr-server-check-" + Guid.NewGuid().ToString("N") + ".yapbak");
            try
            {
                var problem = "";
                while (true)
                {
                    var typed = await AskTextAsync(problem + intro, alternative: "I have lost it");
                    if (typed == null) return null;
                    if (typed.Length == 0) break;
                    if (!File.Exists(temp))
                    {
                        ServerNote("Checking the key against what the server holds…");
                        await client.DownloadAsync(temp);
                        ServerNote("");
                    }
                    var code = RecoveryCode.Normalize(typed);
                    if (code != null && await Task.Run(() => BackupServerClient.CodeOpens(temp, code))) return code;
                    problem = "That key does not open what the server holds. Check it and try again.\n\n";
                }
            }
            finally { try { File.Delete(temp); } catch (IOException) { } }
        }
        var made = RecoveryCode.Generate();
        return await ShowRecoveryCodeAsync(made, isNew: true) ? made : null;
    }

    // ---- backing up ----

    private async void BackupToServerClicked(object? sender, RoutedEventArgs e)
    {
        if (!_server.SignedIn) return;
        BackupToServerButton.IsEnabled = ServerSignOutButton.IsEnabled = false;
        var temp = Path.Combine(Path.GetTempPath(), "yaparr-server-backup-" + Guid.NewGuid().ToString("N") + ".yapbak");
        // An automatic backup may be under way: wait for it, then read what it left (so what is saved below does not undo it).
        ServerNote("Waiting for a sync that is running…");
        await AppHost.Auto.Gate.WaitAsync();
        try
        {
            _server = _data.LoadBackupServer();
            if (!_server.SignedIn) { ShowServerState(); ServerNote("Signed out.", problem: true); return; }
            ServerNote("Checking the server…");
            _data.SaveSettings(_settings);
            using var client = new BackupServerClient(new Uri(_server.Url), _server.Token);
            var versions = await client.VersionsAsync();
            var newest = versions.Count == 0 ? 0 : versions[0].Version;

            if (_server.RecoveryCode.Length == 0)
            {
                if (await ChooseRecoveryCodeAsync(client, newest) is not { } code) { ServerNote("Not synced."); return; }
                _server.RecoveryCode = code;
                _data.SaveBackupServer(_server);
                ShowServerState();
            }
            // Another device may have saved since this one did: this goes on top of it (the server keeps the older copies), so nothing is asked.

            ServerNote("Making the copy…");
            var recoveryCode = _server.RecoveryCode;
            // Taken before the backup is made: what changes while it is being made is then seen as changed, and saved next time.
            var keys = _data.KeysFingerprint();
            await Task.Run(() => _data.ExportProfile(temp, recoveryCode));
            ServerNote("Sending it…");
            var version = await client.UploadAsync(temp, newest);
            _server.RecordBackup(version, keys, DateTimeOffset.UtcNow);
            _server.AutoNote = "";
            _data.SaveBackupServer(_server);
            ShowServerState();
            ServerNote("Synced. It is locked with your Sync key, so the server cannot read it.");
        }
        catch (BackupServerException ex) { ServerFailed(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ServerNote("Sync failed: " + ex.Message, problem: true); }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            AppHost.Auto.Gate.Release();
            BackupToServerButton.IsEnabled = ServerSignOutButton.IsEnabled = true;
        }
    }

    // ---- bringing an account back (first start) ----

    private void ServerRestoreStartClicked(object? sender, RoutedEventArgs e)
    {
        ServerRestoreStatus.Text = "";
        RestoreServerBox.Text = ReleaseConfig.Current.BackupServerToOffer(null);
        RestoreCodeBox.Text = "";
        ShowOnly(ServerRestorePanel);
    }

    private void ServerRestoreBackClicked(object? sender, RoutedEventArgs e)
    {
        _serverRestore?.Cancel();
        ShowOnly(ExistingPanel);
    }

    // On the first screens only, where there is no account yet, so nothing is replaced: sign in, fetch the newest backup, open it with the code, start again.
    private async void ServerRestoreClicked(object? sender, RoutedEventArgs e)
    {
        if (MainActivity.Instance is not { } activity || _data.HasIdentity) return;
        var server = BackupServerClient.ParseServerAddress(RestoreServerBox.Text);
        if (server == null) { ServerRestoreStatus.Text = "The server address is missing or cannot be used. It has to start with https://."; return; }
        var code = RecoveryCode.Normalize(RestoreCodeBox.Text);
        if (code == null) { ServerRestoreStatus.Text = "That is not a Sync key. It is six groups of four letters and digits, like K7QD-M3XF-9PLR-2WTE-HV8N-C4YB."; return; }

        ServerRestoreButton.IsEnabled = RestoreServerBox.IsEnabled = RestoreCodeBox.IsEnabled = false;
        _serverRestore = new CancellationTokenSource();
        var temp = Path.Combine(Path.GetTempPath(), "yaparr-server-restore-" + Guid.NewGuid().ToString("N") + ".yapbak");
        try
        {
            using var signIn = new BrowserSignIn(server, DeviceName(), throughAppLink: true);
            if (!await OpenInBrowserAsync(signIn.LoginUrl)) { ServerRestoreStatus.Text = "Couldn't open your browser."; return; }
            ServerRestoreStatus.Text = "Waiting for you to sign in in your browser. When it says you are signed in, come back to Yaparr.";
            var (token, username) = await signIn.WaitAsync(cancel: _serverRestore.Token);

            ServerRestoreStatus.Text = "Fetching your account…";
            // The account's synced state first (identity and name; the rest comes through the usual sync); an account that holds only whole-profile
            // backups, or whose pieces cannot be opened, goes on to the newest backup as before.
            string? staging = null;
            int version = 0;
            try { staging = await SyncRestore.StageAsync(_data, server, token, code, cancel: _serverRestore.Token); }
            catch (Exception ex) when (ex is BackupServerException or CryptographicException or InvalidDataException or IOException or System.Text.Json.JsonException) { }
            if (staging == null)
            {
                using var client = new BackupServerClient(server, token);
                version = await client.DownloadAsync(temp, cancel: _serverRestore.Token);
                ServerRestoreStatus.Text = "Opening it with your Sync key…";
                staging = await Task.Run(() => _data.StageProfile(temp, code));
            }
            await Task.Run(() => _data.ApplyStagedProfile(staging));

            // This phone stays signed in and keeps the code, so its next backup needs nothing typed.
            _data.SaveBackupServer(new BackupServerState { Url = server.ToString().TrimEnd('/'), Username = username, Token = token, RecoveryCode = code, Version = version });
            ServerRestoreStatus.Text = "Your account is back. Yaparr is starting again.";
            await Task.Delay(1200);
            activity.RestartApp();
        }
        catch (OperationCanceledException) { ServerRestoreStatus.Text = ""; }
        catch (BackupServerException ex) { ServerRestoreStatus.Text = ex.Message; }
        catch (CryptographicException) { ServerRestoreStatus.Text = "That Sync key does not open the newest backup on the server. Check the code; if a newer backup was made with another code, use that one."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { ServerRestoreStatus.Text = ex.Message; }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            ServerRestoreButton.IsEnabled = RestoreServerBox.IsEnabled = RestoreCodeBox.IsEnabled = true;
        }
    }

    // A backup file chosen on the first screens: a plain one opens at once; a locked one asks for its passphrase (a Sync key, or what was typed when it
    // was made on a computer), and asks again after a wrong one. "" if the person backs out.
    private async Task<string> StageBackupFileAsync(string path, TextBlock status)
    {
        if (!AppData.IsBackupLocked(path)) return await Task.Run(() => _data.StageProfile(path));
        var problem = "";
        while (true)
        {
            var typed = await AskTextAsync(problem + "This backup is locked. Type its passphrase or Sync key:", maxLength: 200);
            if (typed == null) { status.Text = ""; return ""; }
            try { return await Task.Run(() => _data.StageProfile(path, typed)); }
            catch (CryptographicException) { problem = "That does not open the backup. Check it and try again.\n\n"; }
        }
    }

    // ---- the button on a backup server's "You're in" page ----

    // yaparr://backup-signin/<code>/<server>. Like every link it only asks: the person is told which server, and whom it would replace, before the
    // one-time code is traded. (The code alone is the proof here, so this question is what stops a made-up link from signing the app in to someone else's account.)
    private void HandleBackupSignInLink(DeepLink link)
    {
        if (!link.TryGetBackupSignIn(out var code, out var server)) return;
        var current = _data.LoadBackupServer();
        var replaces = current.SignedIn
            ? $"\n\nThis phone is signed in to {(Uri.TryCreate(current.Url, UriKind.Absolute, out var now) ? now.Authority : current.Url)} as {current.Username}; this replaces that."
            : "";
        Ask($"A link from the sign-up page of Yaparr Cloud on {server.Authority} asks to sign this phone in to it, so your account stays in step there." +
            "\n\nOnly continue if you have just signed up there and tapped \"Open Yaparr and sign in\"." + replaces, "Sign in", async () =>
        {
            try
            {
                var (token, username) = await BackupServerClient.TradeHandoffCodeAsync(server, code);
                AdoptSignIn(server, token, username);
                SettingsClicked(null, new RoutedEventArgs());
                ServerNote($"Signed in to {server.Authority} as {username}. Tap Sync now to start; Yaparr then makes a Sync key for you to keep.");
            }
            catch (BackupServerException ex) { ListStatus.Text = ex.Message; }
        });
    }
}
