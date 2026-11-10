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
            "Restore a backup: bring back an account from a backup file, for a new or reinstalled computer. It joins your account as another device, so the old computer can stay in use.\n\n" +
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
            // A link file (and a QR code) carries its own passphrase; an older code or a pasted one still asks for it.
            bool carried = LinkQr.TryParse(code, out var carriedCode, out var carriedPassphrase);
            string? passphrase;
            if (carried) { code = carriedCode; passphrase = carriedPassphrase; }
            else passphrase = await PromptDialog.AskAsync(this, "Type the passphrase that came with it (like K7QD-M3XF-9PLR-2WTE-HV8N):");
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
                // A wrong passphrase: keep the code and ask for the passphrase again. (A file that carried its own is damaged: start again.)
                await Dialogs.MessageAsync(this, "Yaparr", ex.Message);
                if (carried) code = null;
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
        await Dialogs.MessageAsync(this, "Restore a backup", "Your account is back on this computer, as another device of it. Yaparr will restart now.");
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
            "This sets up another device of yours as you, with your account, your name and your rooms. Only do it for a device that is yours. " +
            "It can be done with a QR code (quickest for a phone) or with a file you carry to the other device.",
            yes: "Continue", no: "Cancel");
        if (!go) return;

        var node = _node;
        var identity = _identity;
        var passphrase = DeviceLink.GeneratePassphrase();
        var name = node.DisplayName;

        var choice = await Dialogs.ChoiceAsync(this, "Link another device",
            "How do you want to move it to the new device? A QR code is quickest for a phone: the phone scans it and your rooms follow once it is connected. " +
            "A file carries your rooms along with it, and is for moving to a computer.",
            "Show a QR code", "Save to a file…", "Cancel");
        if (choice == "Show a QR code")
        {
            // The code for a QR leaves the room list out: it would not fit. The rooms come from this device once the phone is connected.
            var qrCode = await Task.Run(() => DeviceLink.Create(identity, name, Array.Empty<AccountEntry>(), passphrase));
            await LinkQrDialog.ShowAsync(this, LinkQr.Payload(qrCode, passphrase));
            return;
        }
        if (choice != "Save to a file…") return;

        // The file has everything in it and nothing locks it, so the person is told what that means before it is made.
        var understood = await Dialogs.ConfirmAsync(this, "Keep this file secure",
            "This file is all that is needed to sign in as you on another device, and nothing protects it. Anyone who gets a copy can get into your rooms and act as you, " +
            "and there is no way to take that back afterwards.\n\n" +
            "So please:\n" +
            "•  Move it straight to your own device, for example on a USB stick. Don't email it, put it in cloud storage or send it in a chat.\n" +
            "•  Delete it as soon as the device is linked, and empty the recycle bin.\n\n" +
            "It stops working after a day.",
            yes: "I understand, save the file", no: "Cancel");
        if (!understood) return;

        var entries = node.AccountEntries;
        var code = await Task.Run(() => DeviceLink.Create(identity, name, entries, passphrase));
        var path = await Dialogs.PickSaveAsync(this, "Save the link file", "yaparr-link.ylnk", Dialogs.LinkFile);
        if (path == null) return;
        try { await File.WriteAllTextAsync(path, LinkQr.Payload(code, passphrase) + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Link another device", "Couldn't save the file: " + ex.Message);
            return;
        }
        await Dialogs.MessageAsync(this, "Link another device",
            $"The link file was saved to {path}.\n\nOn the new device, start Yaparr, choose \"Link this device\" and open the file. " +
            "That is all it needs.\n\nDelete the file once the device is linked.");
    }

    // Removes the account from this computer and goes back to the first-start screen. The other devices of the account are not touched.
    private async void LogOut_Click(object? sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        while (true)
        {
            var choice = await Dialogs.ChoiceAsync(this, "Log out",
                "Logging out removes your account from this computer: your identity key, rooms and room keys, message history, pictures and settings. " +
                "Yaparr then restarts at the first-start screen.\n\n" +
                "Your other devices are not affected and keep the account. If this is the only device with the account and you have no backup, " +
                "the account is gone for good and nobody can bring it back.\n\n" +
                "Logging out does not cancel this device's access to the account: it only removes your data from this computer.",
                "Back up first…", "Log out", "Cancel");
            if (choice == "Back up first…") { await BackupProfileAsync(); continue; }
            if (choice != "Log out") return;
            break;
        }

        try { _data.RequestLogOut(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.MessageAsync(this, "Log out", "Couldn't log out: " + ex.Message);
            return;
        }
        _log.Write("logging out: the profile is removed when Yaparr restarts");
        RestartApp();
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
                    // The room is one we are in, and another device has a newer key for it: take that.
                    if (_node != null && RoomIdOf(room.Key) is { } roomId && _roomConversations.Keys.Any(r => r.RoomId == roomId))
                    {
                        await _node.JoinRoomAsync(RoomKey.Parse(room.Key), room.Name);
                        return;
                    }
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
        {
            if (_settings.PrivateRooms.Any(r => r.Key == room.Key)) continue;
            // The same room under a newer key (its owner replaced the key on another device): keep the newer one.
            var same = RoomIdOf(room.Key) is { } id ? _settings.PrivateRooms.FirstOrDefault(r => RoomIdOf(r.Key) == id) : null;
            if (same != null) { if (EpochOf(room.Key) > EpochOf(same.Key)) same.Key = room.Key; }
            else _settings.PrivateRooms.Add(new PrivateRoomSetting { Label = room.Name, Key = room.Key });
            changed = true;
        }
        foreach (var blocked in _node.BlockedPeers)
            if (_settings.BlockedPeers.All(b => b.Id != blocked.Value)) { _settings.BlockedPeers.Add(new KnownPeer { Id = blocked.Value, Name = "", LastSeen = DateTimeOffset.Now }); changed = true; }
        if (changed) _data.SaveSettings(_settings);
    }
}
