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
        var link = await Dialogs.ConfirmAsync(this, "Welcome to Yaparr",
            "Do you already use Yaparr on another device? You can link this one to the same account. Your name, rooms and blocks carry over, and people see you as one person.",
            yes: "Link this device", no: "Start fresh");
        if (!link) return;

        var clipboard = await Dialogs.ClipboardTextAsync(this);
        var initial = clipboard != null && clipboard.StartsWith("YLNK1.", StringComparison.Ordinal) ? clipboard : "";
        while (true)
        {
            var code = await PromptDialog.AskAsync(this, "Paste the link code from your other device (File, Link another device):", initial);
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
            catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
            {
                await Dialogs.MessageAsync(this, "Yaparr", ex.Message);
                initial = code;
            }
        }
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

        string where;
        if (await Dialogs.CopyAsync(this, code)) where = "The code was copied to the clipboard.";
        else
        {
            var path = await Dialogs.PickSaveAsync(this, "Save the link code", "yaparr-link-code.txt", Dialogs.AllFiles);
            if (path == null) return;
            await File.WriteAllTextAsync(path, code);
            where = "The code was saved to " + path + ".";
        }
        await Dialogs.MessageAsync(this, "Link another device",
            $"{where}\n\nOn the new device, start Yaparr, choose \"Link this device\", paste the code, and type this passphrase:\n\n{passphrase}");
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
