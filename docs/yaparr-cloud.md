# Yaparr Cloud: what it does and what it keeps

[Documentation](README.md)

Yaparr works without it. Yaparr Cloud is an optional account on a server (cloud.yaparr.com, or one you run yourself) that does a few things the devices cannot do alone. Everything it holds is **locked on your devices first, with a key the server never has**: the Sync key. The server stores locked bytes it cannot read.

## What it does for you

| | What it is | How long it is kept | Limit |
|---|---|---|---|
| **Sync** | Your name, rooms, blocks, pins and a few settings are the same on all your devices. | Until you change or delete them. | A small record each. |
| **History and shelves** | Your message history (a piece for each month of each room or chat), and your own emoji and GIFs, so a new device has them. | Until you delete them. | 512 MiB. |
| **Messages for people who are away** | A locked copy of a message or picture is left for someone who is offline, and fetched when they are back. | 7 days. | 256 MiB of pictures. |
| **Backups** | A locked copy of your whole profile, so a lost computer can be brought back. | The newest few. | A few hundred MB. |
| **Push** | A wake-up (with nothing in it) so your phone connects when someone writes. | Not kept. | |
| **Gateway** | On a network that blocks UDP, your traffic goes through the server so messages and voice still work. Not screen sharing. | Not kept. | About 196 kbit/s, 2 GiB a day. |

The settings show how full each of these is, and which devices are signed in, with a button to sign one out: **Settings > Yaparr Cloud > Storage and devices** (computer and phone).

## What the server can see

- That your account exists (an email address), when your devices sign in, and their names.
- When your devices sync or fetch something, how much, and from which address.
- For messages left for others: an opaque mailbox id, the size and the time. Not who wrote or what.
- For the gateway: when you used it, how much, and the addresses (IP and port) your traffic went to and came from. Not what is in it: your devices lock that between themselves.

It cannot read your messages, pictures, history, shelves, backups or settings, and it cannot change them without it being noticed.

## Devices

Every device that signs in is listed under **Storage and devices**, with a button to sign it out, and **Sign out all the others** when old sign-ins have piled up. Signing in again does not add another entry for the same device:

- A device makes a random id for itself once (`device-key.txt` in its profile folder, not part of a backup) and sends it when it signs in; the server then replaces that device's earlier entry. A profile restored on a new computer is a new device.
- It also sends a second id derived from the machine itself (Windows' `MachineGuid`, Linux's `/etc/machine-id`, Android's `ANDROID_ID`), so signing in again after the profile was wiped or the app reinstalled, on the same machine, is still one entry. That id is never sent as it is: the server gets a hash of it mixed with the server's address (so two servers cannot tell it is the same machine), and uses it only together with the same device name, because machines cloned from one image share the id.
- Sign-ins from before this (or from an older app) have no id and stay until they are signed out.

## The Sync key

Six groups of four letters and digits. It is made on your first device and shown once. **Nobody can recover it**, Yaparr included: if every device is lost and the key with them, what the server holds cannot be opened. Keep it somewhere safe. If it was lost, or someone else has seen it, **Replace Sync key** locks everything again with a new one and removes what the old key locked; your other devices then ask for the new key.

## Leaving

Deleting your account (Settings > Yaparr Cloud > Delete my account) removes everything above from the server. Signing out on a device only stops that device syncing; what the server holds stays until you delete the account.

More: [Synced state](sync.md), [Mailbox](mailbox.md), [Backup server](backup-server.md), [Connecting across networks](connecting-across-networks.md).
