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
// address and an account by whoever runs it; they sign in in their browser, and the backup is locked here with a Sync key the app makes.
// The app works the same without any of it. The form is on the Backup page of the settings.
public partial class MainWindow
{
    private BackupServerState _server = new();
    private CancellationTokenSource? _serverSignIn;

    // ---- automatic backups (AutoBackup): checked every few minutes while the app runs, even from the notification area ----

    private AutoBackup? _auto;
    private readonly Avalonia.Threading.DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromMinutes(5) };

    // One for the window: it and the backup the person asks for share its gate, so they never run at once.
    private AutoBackup Auto
    {
        get
        {
            if (_auto != null) return _auto;
            _auto = new AutoBackup(_data, log: _log.Write);
            // The state on disk changed (a backup made, paused, failed): what the Backup page shows is read again, on the window's thread.
            _auto.Changed += () => UI(() => { _server = _data.LoadBackupServer(); ShowServerState(); });
            return _auto;
        }
    }

    // Called once the account is known.
    private void StartAutoBackup()
    {
        _autoTimer.Tick += (_, _) => _ = RunAutoBackupAsync();
        _autoTimer.Start();
        _cloudSyncTimer.Tick += (_, _) => _ = RunCloudSyncAsync();
        _cloudSyncTimer.Start();
        // And once soon after the app opens, so a change made while it was closed does not wait the first five minutes.
        Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = RunAutoBackupAsync(), TimeSpan.FromSeconds(45));
        // The name, rooms and blocks are kept the same on all devices through the server as well (AccountCloudSync): once soon after the app opens, every few
        // minutes to hear what other devices did, and shortly after a change made here (a burst of changes is one sync).
        if (_node != null) _node.AccountChanged += () => UI(() => _cloudSyncSoon.Debounce());
        Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = RunCloudSyncAsync(), TimeSpan.FromSeconds(20));
        Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = FirstBackupAtLaunchAsync(), TimeSpan.FromSeconds(10));
        StartMailbox();
    }

    // The settings that are the same on every device (muted rooms, a room's sound, whether messages notify, see SettingsSync) travel as entries of the account: what the
    // account holds is taken in when the app starts, what another device changes is applied as it comes, and what is saved here goes to the account.
    private SettingsPush? _settingsPush;
    private Action<AppSettings>? _settingsSaved;

    private void StartSettingsSync(Yaparr.Chat.ChatNode node)
    {
        var push = _settingsPush = new SettingsPush(node);
        if (push.Seed(_settings)) { _data.SaveSettings(_settings); AfterSettingsChangedElsewhere(node); }
        if (_settingsSaved != null) _data.SettingsSaved -= _settingsSaved;
        _settingsSaved = saved => UI(() => push.Local(saved));
        _data.SettingsSaved += _settingsSaved;
        node.SettingChanged += (name, value) => UI(() =>
        {
            if (!push.Remote(_settings, name, value)) return;
            _data.SaveSettings(_settings);
            AfterSettingsChangedElsewhere(node);
        });
    }

    private void AfterSettingsChangedElsewhere(Yaparr.Chat.ChatNode node)
    {
        node.MailboxLeaving = _settings.LeaveForAbsent;
        LeaveForAbsentCheck.IsChecked = _settings.LeaveForAbsent;
    }

    // The mailbox of each room on the server (MailboxSync): what was written here is left there, locked, for members who are away, and what others left for this device
    // is fetched. Soon after the app opens, every five minutes, and a few seconds after something is written here. Nothing happens until this device is signed in.
    private readonly Avalonia.Threading.DispatcherTimer _mailboxTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private CloudSyncDelay? _mailboxDelay;
    private int _mailboxBusy;

    private void StartMailbox()
    {
        if (_node is not { } node) return;
        node.MailboxEnabled = _data.LoadBackupServer().SignedIn;
        node.MailboxLeaving = _settings.LeaveForAbsent;
        // A small cloud next to the names of people who use Yaparr Cloud (what they say about themselves, see ChatNode.UsesCloud).
        MessageVm.CloudCheck = account => account.Length == 64 && account.All(Uri.IsHexDigit) && _node?.UsesCloud(new PeerId(account)) == true;
        PeerVm.IUseCloud = () => _node?.MailboxEnabled == true;
        VoiceMemberVm.CloudCheck = device => device.All(Uri.IsHexDigit) && _node?.UsesCloudDevice(new PeerId(device)) == true;
        node.CloudUsersChanged += () => UI(() =>
        {
            foreach (var conv in _conversations)
            {
                foreach (var vm in conv.AllMessages) vm.RaiseCloud();
                foreach (var channel in conv.VoiceChannels) foreach (var member in channel.Members) member.RaiseCloud();
            }
            RefreshPeers();   // the people list is made again, so its clouds follow
        });
        _mailboxDelay = new CloudSyncDelay(() => _ = RunMailboxAsync());
        node.MailboxQueued += () => UI(() => _mailboxDelay.Debounce());
        _mailboxTimer.Tick += (_, _) => _ = RunMailboxAsync();
        _mailboxTimer.Start();
        Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = RunMailboxAsync(), TimeSpan.FromSeconds(8));
    }

    private async Task RunMailboxAsync()
    {
        if (_node is not { } node || Interlocked.Exchange(ref _mailboxBusy, 1) == 1) return;
        try { await Task.Run(() => MailboxSync.RunAsync(_data, node, log: _log.Write)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Write("mailbox: " + ex.Message); }
        finally { Interlocked.Exchange(ref _mailboxBusy, 0); }
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

    private readonly Avalonia.Threading.DispatcherTimer _cloudSyncTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private CloudSyncDelay? _cloudSyncDelay;
    private CloudSyncDelay _cloudSyncSoon => _cloudSyncDelay ??= new CloudSyncDelay(() => _ = RunCloudSyncAsync());

    // Waits a few seconds after the last change before it syncs.
    private sealed class CloudSyncDelay
    {
        private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

        public CloudSyncDelay(Action run)
        {
            _timer.Tick += (_, _) => { _timer.Stop(); run(); };
        }

        public void Debounce() { _timer.Stop(); _timer.Start(); }
    }

    private async Task RunCloudSyncAsync()
    {
        if (_node is not { } node) return;
        try { await Task.Run(() => AccountCloudSync.RunAsync(_data, node, log: _log.Write)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Write("sync: " + ex.Message); }
    }

    private async Task RunAutoBackupAsync()
    {
        if (_node == null) return;
        try { await Task.Run(() => Auto.CheckAsync()); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Write("automatic backup: " + ex.Message); }
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")} ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
        return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
    }

    // Called when the Backup page is shown.
    private void FillServerForm()
    {
        _server = _data.LoadBackupServer();
        // The one used before, or the one this release was made for; either can be changed.
        if (string.IsNullOrEmpty(ServerUrlBox.Text)) ServerUrlBox.Text = ReleaseConfig.Current.BackupServerToOffer(_server.Url);
        ShowServerState();
    }

    private void ShowServerState()
    {
        ServerSignedOutPanel.IsVisible = !_server.SignedIn;
        ServerSignedInPanel.IsVisible = _server.SignedIn;
        LeaveForAbsentCheck.IsChecked = _settings.LeaveForAbsent;
        if (!_server.SignedIn) return;
        var host = Uri.TryCreate(_server.Url, UriKind.Absolute, out var uri) ? uri.Authority : _server.Url;
        ServerSignedInText.Text = $"Signed in to {host} as {_server.Username}.";
        ShowRecoveryCodeButton.IsVisible = _server.RecoveryCode.Length > 0;

        var trouble = _server.Failures > 0 || _server.AutoNote.StartsWith("Sync stopped", StringComparison.Ordinal);
        AutoBackupStatus.Text = trouble ? _server.AutoNote
            : _server.RecoveryCode.Length == 0 || _server.Version <= 0 ? "Syncing starts once your Sync key has been made. Press Sync now."
            : _server.LastBackup is { } last ? $"Up to date. Last synced {Ago(last)}."
            : "Syncing by itself.";
        AutoBackupStatus.IsVisible = AutoBackupStatus.Text.Length > 0;
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
            _server = _data.LoadBackupServer();
            _server.Token = "";
            _data.SaveBackupServer(_server);
            ServerUrlBox.Text = ReleaseConfig.Current.BackupServerToOffer(_server.Url);
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

            AdoptSignIn(server, token, username);
            ShowServerState();
            ServerStatusIs("");
            Activate();
            await StartFirstBackupAsync();
        }
        catch (OperationCanceledException) { ServerStatusIs(""); }
        catch (BackupServerException ex) { ServerStatusIs(ex.Message, problem: true); }
        finally
        {
            ServerSignInButton.IsEnabled = ServerUrlBox.IsEnabled = true;
            ServerCancelSignInButton.IsVisible = false;
        }
    }

    // Keeps a sign-in this computer has just been given. Another server or another account than before: its backups have a Sync key of their own.
    private void AdoptSignIn(Uri server, string token, string username)
    {
        var url = server.ToString().TrimEnd('/');
        _server = _data.LoadBackupServer();
        if (url != _server.Url || username != _server.Username) _server = new BackupServerState();
        _server.Url = url;
        _server.Username = username;
        _server.Token = token;
        _data.SaveBackupServer(_server);
        _log.Write("signed in to Yaparr Cloud");
        if (_node != null) { _node.MailboxEnabled = true; _ = RunMailboxAsync(); }
    }

    // The button on a backup server's "You're in" page opened the app: yaparr://backup-signin/<code>/<server>. Like every link it only asks:
    // the person is told which server, and whom it would replace, before the one-time code is traded. (The code alone is the proof here, so this
    // question is what stops a made-up link from signing the app in to someone else's account.)
    private async Task SignInFromLinkAsync(DeepLink link)
    {
        const string title = "Sign in to Yaparr Cloud";
        if (!link.TryGetBackupSignIn(out var code, out var server)) return;
        var current = _data.LoadBackupServer();
        var host = server.Authority;
        var replaces = current.SignedIn
            ? $"\n\nThis computer is signed in to {(Uri.TryCreate(current.Url, UriKind.Absolute, out var now) ? now.Authority : current.Url)} as {current.Username}; this replaces that."
            : "";
        if (!await Dialogs.ConfirmAsync(this, title,
                $"A link from the sign-up page of Yaparr Cloud on {host} asks to sign this computer in to it, so your account stays in step there." +
                "\n\nOnly continue if you have just signed up there and pressed \"Open Yaparr and sign in\"." + replaces,
                "Sign in", "Cancel")) return;

        try
        {
            var (token, username) = await BackupServerClient.TradeHandoffCodeAsync(server, code);
            AdoptSignIn(server, token, username);
            ShowSettings("cloud");
            ServerUrlBox.Text = ReleaseConfig.Current.BackupServerToOffer(_server.Url);
            ShowServerState();
            ServerStatusIs($"Signed in to {host} as {username}. Yaparr makes your Sync key for you to keep, and syncs by itself after that.");
            await StartFirstBackupAsync();
        }
        catch (BackupServerException ex) { await Dialogs.MessageAsync(this, title, ex.Message); }
    }

    // Right after signing in to an account that has no backup yet, the first one starts by itself: it makes the Sync key (shown for the person to keep),
    // which automatic backups and the sync of name, rooms and blocks both need, so nothing waits for the sync first. If the server already has a backup, which
    // another device made, nothing is saved without the person choosing (Back up now asks which code opens it).
    private async Task StartFirstBackupAsync()
    {
        if (_node == null || !_server.SignedIn || _server.RecoveryCode.Length > 0) return;
        try
        {
            using var client = new BackupServerClient(new Uri(_server.Url), _server.Token);
            if ((await client.VersionsAsync()).Count > 0) return;
        }
        catch (BackupServerException ex) { ServerFailed(ex); return; }
        await BackupToServerAsync();
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
        _server = _data.LoadBackupServer();
        _server.Token = "";
        _data.SaveBackupServer(_server);
        ServerUrlBox.Text = ReleaseConfig.Current.BackupServerToOffer(_server.Url);
        ShowServerState();
        ServerStatusIs("Signed out. What Yaparr Cloud holds stays there.");
    }

    private async void ServerChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_server.Url, UriKind.Absolute, out var server))
            await GetTopLevel(this)!.Launcher.LaunchUriAsync(BackupServerClient.PasswordPage(server));
    }

    private void LeaveForAbsent_Click(object? sender, RoutedEventArgs e)
    {
        _settings.LeaveForAbsent = LeaveForAbsentCheck.IsChecked == true;
        _data.SaveSettings(_settings);
        if (_node != null) _node.MailboxLeaving = _settings.LeaveForAbsent;
    }

    private async void ServerDeleteAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(_server.Url, UriKind.Absolute, out var server))
            await GetTopLevel(this)!.Launcher.LaunchUriAsync(BackupServerClient.DeleteAccountPage(server));
    }

    private async void ShowRecoveryCode_Click(object? sender, RoutedEventArgs e)
    {
        if (_server.RecoveryCode.Length > 0) await ShowRecoveryCodeAsync(_server.RecoveryCode, isNew: false);
    }

    // Shows the Sync key with a button to copy it. A new code cannot be dismissed before it has been copied (or copying failed, and it has to be written
    // down): there is no Cancel, the window's close button does nothing, and "I have saved it" waits for the copy. Only then does the backup go on.
    private async Task<bool> ShowRecoveryCodeAsync(string code, bool isNew)
    {
        bool saved = false, copied = !isNew;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var dialog = new Window
        {
            Title = "Sync key",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        Button? doneButton = null;
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) =>
        {
            var ok = await Dialogs.CopyAsync(dialog, code);
            copy.Content = ok ? "Copied" : "Couldn't copy: write it down";
            copied = true;
            if (doneButton != null) doneButton.IsEnabled = true;
        };
        buttons.Children.Add(copy);
        if (isNew)
        {
            var done = doneButton = new Button { Content = "I have saved it", IsDefault = true, IsEnabled = false };
            done.Classes.Add("primary");
            done.Click += (_, _) => { saved = true; dialog.Close(); };
            buttons.Children.Add(done);
            dialog.Closing += (_, e) => { if (!saved) e.Cancel = true; };
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
                        ? "This is your Sync key. It locks everything Yaparr Cloud holds for you, so the server cannot read it. You need it to set up a computer that has nothing, " +
                          "and nobody can recover it for you: not the server, and not your password.\n\nSave it now, in a password manager or on paper."
                        : "Your Sync key. It locks what Yaparr Cloud holds for you, and you need it to set up a computer that has nothing. Keep it somewhere safe and private.",
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
        const string title = "Sync with Yaparr Cloud";
        if (newest > 0)
        {
            var choice = await Dialogs.ChoiceAsync(this, title,
                "This account already has data on Yaparr Cloud, from another device. Type that device's Sync key (Settings > Yaparr Cloud > Show Sync key there) so all your devices use the same one." +
                "\n\nIf you have lost it, you can start a new one here, but what is already stored cannot be opened with it.",
                "Type the Sync key", "I have lost it", "Cancel");
            if (choice == "Type the Sync key")
            {
                var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-check-" + Guid.NewGuid().ToString("N") + ".yapbak");
                try
                {
                    ServerStatusIs("Checking the key against what the server holds…");
                    await client.DownloadAsync(temp);
                    ServerStatusIs("");
                    while (true)
                    {
                        var typed = await PromptDialog.AskAsync(this, "The Sync key of your other device:");
                        if (typed == null) return null;
                        var code = RecoveryCode.Normalize(typed);
                        if (code != null && await Task.Run(() => BackupServerClient.CodeOpens(temp, code))) return code;
                        await Dialogs.MessageAsync(this, title, "That key does not open what the server holds. Check it and try again.");
                    }
                }
                finally { try { File.Delete(temp); } catch (IOException) { } }
            }
            if (choice != "I have lost it") return null;
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
        // An automatic backup may be under way: wait for it, then read what it left (so what is saved below does not undo it).
        ServerStatusIs("Waiting for a sync that is running…");
        await Auto.Gate.WaitAsync();
        try
        {
            _server = _data.LoadBackupServer();
            if (!_server.SignedIn) { ShowServerState(); ServerStatusIs("Signed out.", problem: true); return; }
            ServerStatusIs("Checking the server…");
            using var client = new BackupServerClient(new Uri(_server.Url), _server.Token);
            var versions = await client.VersionsAsync();
            var newest = versions.Count == 0 ? 0 : versions[0].Version;

            if (_server.RecoveryCode.Length == 0)
            {
                if (await ChooseRecoveryCodeAsync(client, newest) is not { } code) { ServerStatusIs("Not synced."); return; }
                _server.RecoveryCode = code;
                _data.SaveBackupServer(_server);
                ShowServerState();
            }
            // Another device may have saved since this one did: this goes on top of it (the server keeps the older copies), so nothing is asked.

            ServerStatusIs("Making the copy…");
            var recoveryCode = _server.RecoveryCode;
            // Taken before the backup is made: what changes while it is being made is then seen as changed, and saved next time.
            var keys = _data.KeysFingerprint();
            await Task.Run(() => _data.ExportProfile(temp, recoveryCode));
            ServerStatusIs("Sending it…");
            var version = await client.UploadAsync(temp, newest);
            _server.RecordBackup(version, keys, DateTimeOffset.UtcNow);
            _server.AutoNote = "";
            _data.SaveBackupServer(_server);
            _log.Write($"backed up to a server (version {_server.Version})");
            ShowServerState();
            ServerStatusIs("Synced. It is locked with your Sync key, so the server cannot read it.");
        }
        catch (BackupServerException ex) { ServerFailed(ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ServerStatusIs("Sync failed: " + ex.Message, problem: true); }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            Auto.Gate.Release();
            BackupToServerButton.IsEnabled = ServerSignOutButton.IsEnabled = true;
        }
    }

    // On the first-start screen of a computer with no account (there is no settings page yet): sign in and bring the account back from the newest copy.
    private async Task RestoreFromServerAsync()
    {
        const string title = "Restore from Yaparr Cloud";
        if (await ServerLoginDialog.AskAsync(this) is not { } login) return;
        string? staging = null;
        int newest = 0;
        // The account's synced state is tried first: the identity and name are fetched and the rest comes through the usual sync. An account that
        // holds only whole-profile backups (or whose pieces cannot be opened) goes on to the newest backup, as before.
        try { staging = await SyncRestore.StageAsync(_data, login.Server, login.Token, login.RecoveryCode); }
        catch (Exception ex) when (ex is BackupServerException or CryptographicException or InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            _log.Write("restoring from the synced state did not work: " + ex.Message);
        }
        if (staging == null)
        {
            using (var client = new BackupServerClient(login.Server, login.Token))
            {
                IReadOnlyList<ServerBackupVersion> versions;
                try { versions = await client.VersionsAsync(); }
                catch (BackupServerException ex) { await Dialogs.MessageAsync(this, title, ex.Message); return; }
                if (versions.Count == 0) { await Dialogs.MessageAsync(this, title, "Yaparr Cloud holds nothing for this account yet."); return; }
                newest = versions[0].Version;

                // The newest copy is brought back; if it cannot be opened (damaged), the next older one is tried, so nothing is asked.
                bool opened = true;
                foreach (var version in versions)
                {
                    var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-restore-" + Guid.NewGuid().ToString("N") + ".yapbak");
                    try
                    {
                        await client.DownloadAsync(temp, version.Version);
                        staging = await Task.Run(() => _data.StageProfile(temp, login.RecoveryCode));
                        break;
                    }
                    catch (BackupServerException ex) { await Dialogs.MessageAsync(this, title, ex.Message); return; }
                    catch (Exception ex) when (ex is CryptographicException or InvalidDataException) { opened = false; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        await Dialogs.MessageAsync(this, title, ex.Message);
                        return;
                    }
                    finally { try { File.Delete(temp); } catch (IOException) { } }
                }
                if (staging == null)
                {
                    await Dialogs.MessageAsync(this, title, opened ? "Nothing could be restored." : "That Sync key does not open what Yaparr Cloud holds. Check the code; if a newer copy was made with another code, use that one.");
                    return;
                }
            }
        }

        try { _data.ApplyStagedProfile(staging); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, title, "Restoring failed: " + ex.Message);
            return;
        }
        // This computer stays signed in and keeps the code, so its next backup needs nothing typed.
        _data.SaveBackupServer(new BackupServerState
        {
            Url = login.Server.ToString().TrimEnd('/'), Username = login.Username, Token = login.Token, RecoveryCode = login.RecoveryCode, Version = newest,
        });
        _log.Write("restored a backup from a server");
        await Dialogs.MessageAsync(this, title, "Your account is back on this computer, as another device of it. Yaparr will restart now.");
        RestartApp();
    }
}
