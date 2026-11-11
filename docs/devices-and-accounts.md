# Devices and accounts

[Documentation](README.md)

## Changing your name

`ChatNode.Rename(name)` changes the name others see (trimmed, at most 64 characters, not empty). Everyone you are
connected to is told straight away (`PeerRenamed` and `ChatRoom.MemberRenamed` on their side), and your presence in each
room and in the voice channel is announced again under the new name, so people you are not directly connected to see it
as well. New messages carry the new name; messages already sent are signed with the name they had and keep it. In the
Desktop app it is **the pencil that shows when you point at your name above the conversations**, and it is remembered for the next start.

## Using several devices

One person can use Yaparr on several devices. Every device has a key of its own, and the first device's key is the
account key. A device linked later gets a certificate from the account key, which it shows in its hello, so everyone it
meets can tell it belongs to the same person (`PeerInfo.Account`, `ChatNode.AccountId`). Older versions ignore the
certificate and see another device as another person with the same name. Each device is an ordinary peer, so
connections, relays, voice and delivery work per device; what is shared is the person.

- **Linking.** On a device you already use choose **Settings > Devices > Link another device…** and pick a QR code or a file. Either way the device makes a code
  that holds a new identity for the other device plus your name, rooms and blocks, encrypted with a passphrase (about 100 bits) that is made for it and travels with it,
  so the person types nothing. **Show a QR code** (for a phone) shows the code and its passphrase together as one QR code for two minutes, and the Android app scans it
  (**Yes, I already have an account** > **On my computer**). The QR code leaves your list of rooms out, because it would not fit; the rooms follow from the computer once the
  phone has connected to it. **Save to a file…** makes one self-contained link file (`.ylnk`, a text file with the code and its passphrase), with the rooms in it. Because nothing
  locks the file, a **Keep this file secure** warning comes first: it says that anyone who gets a copy can get into your rooms and act as you with no way to take that back, to move it
  straight to your own device (a USB stick, not email, cloud storage or chat), to delete it once the device is linked, and that it stops working after a day. On the new device choose
  **Link this device** at first start and open the file (or paste a code; an older code, made without its passphrase, still asks for one). The code stops working after a day. Show a QR code
  only to someone next to you. In the library: `DeviceLink.Create` and `DeviceLink.Open`, and `LinkQr.Payload` and `LinkQr.TryParse` for the code with its passphrase.
- **Finding each other.** Devices of one account find each other without a room in common: each announces itself on the account's topic and
  every few seconds, while it is connected to none of its other devices, looks there and connects to what it finds (and less often when one is
  connected). A phone that was just linked and has no rooms yet therefore still meets your computers, and receives the name, blocks and rooms.
- **Backup and restore.** **Settings > Backup > Back up profile…** saves a copy of everything Yaparr keeps for you: your identity key, rooms, message history, pictures and settings. The desktop app offers to **lock it with a passphrase** (a `.yapbak` file) or to save a plain zip. A locked backup is safe to keep anywhere, such as a cloud drive or an email to yourself; a plain zip lets anyone who gets it act as you. The lock is `ProfileCipher`: the key comes from the passphrase with Argon2id (64 MiB, three passes; the settings are stored in the file, so they can be raised later), and the data is sealed in 64 KiB chunks with AES-GCM, each tied to its place and to the header and the last one marked, so a changed, reordered, mixed or cut-short file is noticed. The zip is written straight into the lock, so the plain backup never exists as a file. The passphrase needs at least 12 characters, is typed twice, and cannot be recovered if lost; a wrong passphrase and a damaged file look the same from outside. Restoring asks for the passphrase when the file is locked, and asks again on a mistake. Android makes plain backups and cannot yet open a locked one. A locked backup can also be kept on a backup server (**Settings > Backup > Backup server**, and **Restore from a server** on the first-start screen): you sign in in your browser, and the backup is locked with a recovery code the app makes, see [backup-server.md](backup-server.md). To restore it, choose **Restore a backup** on the first-start screen of a computer that has no account yet. There is no restore for a computer that already has an account, so nothing is replaced by accident. A restored backup becomes another device of the account, not a copy of the one it came from: `AppData.StageProfile` keeps the account key and gives the computer a device key of its own, vouched for by the account key, exactly as when a device is linked. So a backup can be restored on a second computer while the original is still in use, or on several, and each is a separate device of one person (friends see the same account id); nothing clashes. The device the backup came from keeps its own key. Restore, a QR code and a link file are three ways of adding a device to your account.
- **Logging out.** **Settings > Devices > Log out…** removes the account from this computer (identity key, rooms and room keys, history, pictures and settings; the FFmpeg program, updates and logs stay) and restarts Yaparr at the first-start screen, where you can start fresh, link, or restore a backup. It offers a backup first. The running app does not delete anything itself: `AppData.RequestLogOut` leaves a note (`logout.pending`) holding its process id, and at the next start `AppData.CompleteLogOut` waits for that process to exit and then removes the profile, so open files and late settings saves cannot bring it back. Your other devices are not touched. Computer app only so far.
- **Sharing your id.** Give people your account id (**Copy my ID**), which is the same on every device. A direct chat
  with a person reaches all of their devices, and counts as delivered when every connected device confirmed it.
- **What stays in step.** Your name, blocks and the list of rooms follow you between devices
  (`ChatNode.SyncedRoomChanged`, `DisplayNameChanged`, `AccountEntries`), over connections whose certificate proves they
  are your own. Changes are kept in `account.json` and a device that was off catches up when it meets another; the newest
  change wins. Room history syncs between your devices like it does between any two members of the room.
- **People.** The Desktop app lists a person once however many devices they use, and does not list your own other devices.
  A block applies to the person, with all of their devices.
- **Direct messages.** A direct message you send or receive is kept (for the same 7 days and 500 messages as a room) and copied to your other devices, and two of your devices that meet swap what either is missing, so a new or switched-off device catches up. Attachments are fetched from your other devices too, and which messages you have read is kept in step. Each direct message is signed by the device that wrote it, with the account's certificate for that device and the recipient inside the signature, and your devices check that before they accept a copy, so one device cannot make up a conversation. Messages from older versions have no signature: they are shown but stay on the device that got them.
- **One device in voice.** Joining voice in a room on one device tells your other connected devices, which leave it (`Wire.Takeover`, live only, nothing replayed later); two that join at the same moment settle it by device id.
- **My devices.** The table under Settings > Devices lists this device and the others of your account that are connected, and a message you sent from another device says which one next to its time.
- **Blocks.** A linked device's presence beacon carries its certificate, so a block of the person reaches a device we have never connected to, once its beacon has reached us.

- **Screen sharing.** A linked device that is not in a voice call can share its screen into the call another of your devices is in (see [Sharing a screen](screen-sharing.md#sharing-from-a-linked-device)); with none of your devices in the call it cannot.

Limit: every linked device holds the account key, so there is no way to take a lost device away from the account (it would need a new identity). Logging out removes the data from a computer but does not cancel what it could do with a copy of it. Details are in the [backlog](../BACKLOG.md).

Why it works this way: this replaced the first plan (one key on every device, with connections keyed by peer and device ID), which would have meant reworking every table keyed by peer ID and made older clients fight over the one slot.
