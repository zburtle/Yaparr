using System.Security.Cryptography;
using Avalonia.Interactivity;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Desktop;

// Using one account on several devices. A new device is linked with a code made on one you already use; after
// that your name, blocks and rooms follow you from device to device.
public partial class MainWindow
{
    private PeerIdentity? _identity;
    private IReadOnlyList<AccountEntry> _linkedEntries = Array.Empty<AccountEntry>();

    // First start on a computer with no profile: offer to join an account that already exists.
    private async Task OfferLinkAsync()
    {
        var start = await Dialogs.ChoiceAsync(this, "Welcome to Yaparr",
            "Do you already have a Yaparr account?\n\n" +
            "Link this device: join the account of a device you still use. Your name, rooms and blocks carry over, and people see you as one person.\n\n" +
            "Restore a backup: bring back an account from a backup file, for a new or reinstalled computer. Don't restore one while the old computer is still in use; link instead.\n\n" +
            "Start fresh: make a new account.",
            "Link this device", "Restore a backup", "Start fresh");
        if (start == "Restore a backup") { await RestoreBackupAsync(); return; }
        if (start != "Link this device") return;

        var clipboard = await Dialogs.ClipboardTextAsync(this);
        var initial = clipboard != null && clipboard.StartsWith("YLNK1.", StringComparison.Ordinal) ? clipboard : "";
        string? code = null;
        while (true)
        {
            code ??= await AskForLinkCodeAsync(initial);
            if (string.IsNullOrWhiteSpace(code)) return;
            var passphrase = await PromptDialog.AskAsync(this, "Type the passphrase that came with it (like K7QD-M3XF-9PLR-2WTE-HV8N):");
            if (string.IsNullOrWhiteSpace(passphrase)) return;
            try
            {
                var linked = await Task.Run(() => DeviceLink.Open(code, passphrase));
                _data.SaveIdentity(linked.Identity);
                linked.Identity.Dispose();
                _settings.DisplayName = linked.DisplayName;
                _data.SaveSettings(_settings);
                _linkedEntries = linked.Entries;
                _log.Write("linked this device to an existing account");
                return;
            }
            catch (CryptographicException ex)
            {
                // A wrong passphrase: keep the code and ask for the passphrase again.
                await Dialogs.MessageAsync(this, "Yaparr", ex.Message);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                await Dialogs.MessageAsync(this, "Yaparr", ex.Message);
                initial = code;
                code = null;
            }
        }
    }

    // The code from the other device, from a link file or pasted. Null if the user backs out.
    private async Task<string?> AskForLinkCodeAsync(string initial)
    {
        var how = await Dialogs.ChoiceAsync(this, "Link this device",
            "How do you want to bring in the link code from your other device (File, Link another device)?",
            "Open a link file…", "Paste a code", "Cancel");
        if (how == "Paste a code")
            return await PromptDialog.AskAsync(this, "Paste the link code from your other device:", initial);
        if (how != "Open a link file…") return null;

        var path = await Dialogs.PickOpenAsync(this, "Open the link file", Dialogs.LinkFile, Dialogs.AllFiles);
        if (path == null) return null;
        try
        {
            if (new FileInfo(path).Length > 1_000_000) throw new IOException("That file is too big to be a link file.");
            return (await File.ReadAllTextAsync(path)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Yaparr", "Couldn't read that file: " + ex.Message);
            return null;
        }
    }

    // Brings back a backup made with File, Back up profile. Only offered where there is no account yet, so nothing is replaced.
    private async Task RestoreBackupAsync()
    {
        var path = await Dialogs.PickOpenAsync(this, "Restore a backup", Dialogs.Zip);
        if (path == null) return;

        string staging;
        try { staging = _data.StageProfile(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Restore a backup", ex.Message);
            return;
        }

        try
        {
            _data.ApplyStagedProfile(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Restore a backup", "Restoring failed: " + ex.Message);
            return;
        }
        _log.Write("restored a backup");
        await Dialogs.MessageAsync(this, "Restore a backup", "Your account is back. Yaparr will restart now.");
        RestartApp();
    }

    private async void LinkDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null || _identity == null) return;
        if (!_identity.CanLinkDevices)
        {
            await Dialogs.MessageAsync(this, "Link another device", "This device cannot link others.");
            return;
        }
        var go = await Dialogs.ConfirmAsync(this, "Link another device",
            "This makes a code for a new device. It holds your account key, so anyone who gets both the code and its passphrase can act as you. " +
            "Send them by different routes, and delete the code afterwards. It stops working in a day.",
            yes: "Make a code", no: "Cancel");
        if (!go) return;

        var node = _node;
        var identity = _identity;
        var passphrase = DeviceLink.GeneratePassphrase();
        var entries = node.AccountEntries;
        var name = node.DisplayName;
        var code = await Task.Run(() => DeviceLink.Create(identity, name, entries, passphrase));

        var choice = await Dialogs.ChoiceAsync(this, "Link another device",
            "Your code is ready. How do you want to move it to the new device?", "Save to a file…", "Copy to clipboard", "Cancel");
        string where, step;
        if (choice == "Save to a file…")
        {
            var path = await Dialogs.PickSaveAsync(this, "Save the link file", "yaparr-link.ylnk", Dialogs.LinkFile);
            if (path == null) return;
            try { await File.WriteAllTextAsync(path, code + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.MessageAsync(this, "Link another device", "Couldn't save the file: " + ex.Message);
                return;
            }
            where = "The link file was saved to " + path + ". Copy it to the new device, for example on a USB stick.";
            step = "open the link file";
        }
        else if (choice == "Copy to clipboard")
        {
            if (!await Dialogs.CopyAsync(this, code))
            {
                await Dialogs.MessageAsync(this, "Link another device", "Couldn't copy: another program is using the clipboard. Try again, or save to a file.");
                return;
            }
            where = "The code was copied to the clipboard.";
            step = "paste the code";
        }
        else return;
        await Dialogs.MessageAsync(this, "Link another device",
            $"{where}\n\nOn the new device, start Yaparr, choose \"Link this device\", {step}, and type this passphrase:\n\n{passphrase}");
    }

    // Another of your devices added or removed a room.
    private void OnSyncedRoomChanged(SyncedRoom room, bool joined)
    {
        UI(async () =>
        {
            try
            {
                if (joined)
                {
                    if (_settings.PrivateRooms.Any(r => r.Key == room.Key)) return;
                    await JoinKeyedRoomAsync(room.Name, RoomKey.Parse(room.Key), persist: true);
                    StatusText.Text = $"Joined \"{room.Name}\" because you joined it on another device.";
                }
                else if (_conversations.FirstOrDefault(c => c.Invite == room.Key) is { } conv)
                {
                    await LeaveConversationAsync(conv);
                    StatusText.Text = $"Left \"{room.Name}\" because you left it on another device.";
                }
            }
            catch (Exception ex) { _log.Write($"could not apply a room change from another device: {ex.Message}"); }
        });
    }

    // Another of your devices changed your name.
    private void OnDisplayNameChanged(string name) => UI(() =>
    {
        _settings.DisplayName = name;
        _data.SaveSettings(_settings);
        ShowOwnName(name);
        StatusText.Text = $"You are now {name}, changed on another device.";
    });

    // Rooms and blocks the account already has, from a linked device or an earlier run, join the lists kept in the settings.
    private void ReconcileFromAccount()
    {
        if (_node == null) return;
        bool changed = false;
        foreach (var room in _node.SyncedRooms)
            if (_settings.PrivateRooms.All(r => r.Key != room.Key)) { _settings.PrivateRooms.Add(new PrivateRoomSetting { Label = room.Name, Key = room.Key }); changed = true; }
        foreach (var blocked in _node.BlockedPeers)
            if (_settings.BlockedPeers.All(b => b.Id != blocked.Value)) { _settings.BlockedPeers.Add(new KnownPeer { Id = blocked.Value, Name = "", LastSeen = DateTimeOffset.Now }); changed = true; }
        if (changed) _data.SaveSettings(_settings);
    }
}
