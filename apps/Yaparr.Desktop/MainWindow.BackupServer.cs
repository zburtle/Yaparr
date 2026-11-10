using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Yaparr.AppCore;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Keeping a locked backup on a backup server (BackupServerClient), so it can be fetched from any computer. The person is given the server's
// address and an account by whoever runs it; the app works the same without one. The form is on the Backup page of the settings.
public partial class MainWindow
{
    // The address and name are remembered between runs; the password never is.
    private void FillServerForm()
    {
        if (string.IsNullOrEmpty(ServerUrlBox.Text)) ServerUrlBox.Text = _settings.BackupServerUrl;
        if (string.IsNullOrEmpty(ServerUserBox.Text)) ServerUserBox.Text = _settings.BackupServerUser;
    }

    // The message goes under the button that was pressed: the backup's, or the password change's.
    private void ServerStatusIs(string text, bool problem = false, bool forPassword = false)
    {
        var target = forPassword ? ChangePasswordStatus : ServerStatus;
        target.Text = text;
        target.IsVisible = text.Length > 0;
        var name = problem ? "Bad" : "Muted";
        target.Foreground = this.TryFindResource(name, ActualThemeVariant, out var found) && found is Avalonia.Media.IBrush brush ? brush : Avalonia.Media.Brushes.Gray;
    }

    private void SetServerBusy(bool busy)
    {
        BackupToServerButton.IsEnabled = ChangeServerPasswordButton.IsEnabled = !busy;
    }

    private ServerLoginForm? ReadServerForm(bool forPassword = false)
    {
        var form = BackupServerForm.Read(ServerUrlBox.Text, ServerUserBox.Text, forPassword ? CurrentServerPasswordBox.Text : ServerPasswordBox.Text, out var problem);
        if (form == null) ServerStatusIs(problem, problem: true, forPassword);
        return form;
    }

    private void RememberServer(ServerLoginForm form)
    {
        _settings.BackupServerUrl = form.Server.ToString().TrimEnd('/');
        _settings.BackupServerUser = form.Username;
        _data.SaveSettings(_settings);
    }

    private async void BackupToServer_Click(object? sender, RoutedEventArgs e) => await BackupToServerAsync();

    private async Task BackupToServerAsync()
    {
        if (_node == null || ReadServerForm() is not { } form) return;
        SetServerBusy(true);
        var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-backup-" + Guid.NewGuid().ToString("N") + ".yapbak");
        try
        {
            ServerStatusIs("Checking the login…");
            // Making the key from the password takes a moment on purpose.
            using var client = await Task.Run(() => new BackupServerClient(form.Server, form.Username, form.Password));
            var versions = await client.VersionsAsync();
            var sameServer = form.Server.ToString().TrimEnd('/') == _settings.BackupServerUrl && form.Username == _settings.BackupServerUser;
            RememberServer(form);
            var newest = versions.Count == 0 ? 0 : versions[0].Version;

            // A newer backup than the one this computer last saved or restored came from somewhere else; do not add to it without being told.
            if (newest > 0 && !(sameServer && newest == _settings.BackupServerVersion)
                && !await Dialogs.ConfirmAsync(this, "Back up to a server",
                    $"The server has a backup (version {newest}, {versions[0].At.LocalDateTime:g}) that this computer did not make or has not seen. Saving now adds a newer one; the older ones are kept for a while.",
                    "Save anyway", "Cancel")) { ServerStatusIs("Backup not saved."); return; }

            ServerStatusIs("Making the backup…");
            await Task.Run(() => _data.ExportProfile(temp, form.Password));
            ServerStatusIs("Sending the backup…");
            var version = await client.UploadAsync(temp, newest);
            _settings.BackupServerVersion = version;
            _data.SaveSettings(_settings);
            _log.Write($"backed up to a server (version {version})");
            ServerPasswordBox.Text = "";
            ServerStatusIs($"Backed up to the server (version {version}). It is locked with your password, so the server cannot read it.");
        }
        catch (BackupServerException ex) { ServerStatusIs(ex.Message, problem: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ServerStatusIs("Backup failed: " + ex.Message, problem: true); }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            SetServerBusy(false);
        }
    }

    private async void ChangeServerPassword_Click(object? sender, RoutedEventArgs e) => await ChangeServerPasswordAsync();

    private async Task ChangeServerPasswordAsync()
    {
        if (_node == null || ReadServerForm(forPassword: true) is not { } form) return;
        var replacement = NewServerPasswordBox.Text ?? "";
        if (BackupServerClient.CheckPassword(replacement) is { } problem) { ServerStatusIs("The new password: " + problem, problem: true, forPassword: true); return; }
        if (replacement != NewServerPasswordAgainBox.Text) { ServerStatusIs("The two new passwords are not the same.", problem: true, forPassword: true); return; }
        if (replacement == form.Password) { ServerStatusIs("The new password is the same as the current one.", problem: true, forPassword: true); return; }

        SetServerBusy(true);
        var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-backup-" + Guid.NewGuid().ToString("N") + ".yapbak");
        try
        {
            ServerStatusIs("Changing the password…", forPassword: true);
            using var client = await Task.Run(() => new BackupServerClient(form.Server, form.Username, form.Password));
            var versions = await client.VersionsAsync();
            RememberServer(form);
            await client.ChangePasswordAsync(replacement);
            // The backups on the server are still locked with the old password, so a new one is made at once, locked with the new.
            ServerStatusIs("Saving a new backup with the new password…", forPassword: true);
            using var changed = await Task.Run(() => new BackupServerClient(form.Server, form.Username, replacement));
            await Task.Run(() => _data.ExportProfile(temp, replacement));
            _settings.BackupServerVersion = await changed.UploadAsync(temp, versions.Count == 0 ? 0 : versions[0].Version);
            _data.SaveSettings(_settings);
            _log.Write("changed the backup server password");
            ServerPasswordBox.Text = CurrentServerPasswordBox.Text = NewServerPasswordBox.Text = NewServerPasswordAgainBox.Text = "";
            ServerStatusIs("The password is changed, and a new backup is saved with it. Older backups on the server still need the old password. Use the new one from now on.", forPassword: true);
        }
        catch (BackupServerException ex) { ServerStatusIs(ex.Message, problem: true, forPassword: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ServerStatusIs("Couldn't make the new backup: " + ex.Message, problem: true, forPassword: true); }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            SetServerBusy(false);
        }
    }

    // On the first-start screen of a computer with no account (there is no settings page yet): fetch the newest backup from a server and bring it back.
    private async Task RestoreFromServerAsync()
    {
        const string title = "Restore from a server";
        if (await ServerLoginDialog.AskAsync(this) is not { } form) return;
        var temp = Path.Combine(Path.GetTempPath(), "Yaparr-server-restore-" + Guid.NewGuid().ToString("N") + ".yapbak");
        string staging;
        try
        {
            using var client = await Task.Run(() => new BackupServerClient(form.Server, form.Username, form.Password));
            await client.DownloadAsync(temp);
            staging = await Task.Run(() => _data.StageProfile(temp, form.Password));
        }
        catch (BackupServerException ex) { await Dialogs.MessageAsync(this, title, ex.Message); return; }
        catch (CryptographicException)
        {
            await Dialogs.MessageAsync(this, title, "The backup on the server could not be opened with that password. If the password was changed, the backup may still be locked with the old one.");
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
        // Remembered for the next backup, so the server and name do not have to be typed again.
        var restored = _data.LoadSettings();
        restored.BackupServerUrl = form.Server.ToString().TrimEnd('/');
        restored.BackupServerUser = form.Username;
        _data.SaveSettings(restored);
        _log.Write("restored a backup from a server");
        await Dialogs.MessageAsync(this, title, "Your account is back on this computer, as another device of it. Yaparr will restart now.");
        RestartApp();
    }
}
