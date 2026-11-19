# Yaparr Cloud (sync)

Yaparr Cloud keeps your account **in step on your devices**: your name, rooms and their keys, blocks, pins and (as a locked copy) your message history, so a new computer or phone can pick up where you left off, and a device that was off catches up when it is back. Everything it holds is **locked** on your device with your **Sync key** first, so the server stores what it cannot read. It is optional: Yaparr works the same without it, and nothing is sent anywhere unless you use it. Whoever runs a server gives you its address and an account on it.

There is nothing to manage: no versions, no "newest" copy to choose, no questions between your own devices. The screens say "synced", and when it last did. (Underneath, the account's entries are merged one by one, see [Synced state](sync.md); the message history is still saved as one locked copy for now, and the server keeps a few older copies of it as a safety net that nobody has to look at.)

## Using it

**Settings > Yaparr Cloud**, on the desktop and on Android (the screens are the same; on a phone the sign-in comes back to the app through the phone's browser, so stay with it until the page says you are signed in and then come back to Yaparr):

1. The address is filled in for you: releases of Yaparr are made for **https://cloud.yaparr.com** (see below), and the address of the last server you used is offered instead if you have used another. You can type a different one. Choose **Sign in with your browser**. Your browser opens the server's sign-in page; type your email address and password there (an account is named by its owner's email address, which the person running the server enters when they make it; nothing is ever sent to it). Yaparr never sees them. When you are signed in the browser says so and Yaparr shows "Signed in to ... as ...".
2. The first time, Yaparr makes your **Sync key** (like `K7QD-M3XF-9PLR-2WTE-HV8N-C4YB`) and shows it once: save it in a password manager or on paper. After that, syncing needs nothing typed. **Sync now** syncs at once instead of waiting for the next time Yaparr would.
3. **Show Sync key** shows it again on a device that has it. **Change password** opens the server's page for that. **Sign out** makes the server forget this device; what the server holds stays.

**Starting from the sign-up page.** When someone signs up in their browser, the server's "You're in" page has an **Open Yaparr and sign in** button (on a computer and on an Android phone; not on an iPhone, which has no app). It opens Yaparr, which asks "Sign in to Yaparr Cloud ...?" and names the server (and the account it would replace, if this computer is already signed in somewhere). On **Sign in** the app is signed in and opens Settings > Yaparr Cloud, so the password is not typed twice. If Yaparr is not installed or is older than this feature, nothing happens and the page's steps (above) still work. The Sync key is made at the first sync.

**Syncing by itself.** Syncing is **always on while you are signed in** (there is no switch; signing out stops it). It starts with the first sync, which Yaparr begins by itself right after you sign in to an account that holds nothing yet, and when the app opens if a signed-in account still has none, since that is when your Sync key is made. The Sync key has to be copied before that window can be closed. If the server already holds data from another device, nothing is saved until you press **Sync now**, which asks for that device's Sync key (so every device uses the same one). While Yaparr is running, even in the notification area, it checks every five minutes and saves:

- **soon after what cannot be lost changes**: a room joined or made, a room key, a linked device, your name or the account. The change has to be still for two minutes, so joining three rooms in a row is one save. A fingerprint of that (`AppData.KeysFingerprint`) is saved each time and compared with the current one; it does not change when messages arrive or a setting such as the theme is changed;
- **once a day for the history**, if any message, direct chat, picture or sound has been written since the last save.

It never asks anything. If another device saved since this one did, this one goes on top of it, and the server keeps the older copies, so there is no "newer copy" to choose between. If the server no longer knows this device it stops and says so; if the server cannot be reached it tries again after five minutes, then ten, doubling up to six hours, and says how long it has been failing once that is three days. The status line says whether it is up to date and when it last synced. A phone does the same while the app is running (it checks every five minutes, and once shortly after the app opens).

Because saving can happen often, the server keeps the newest three versions and the newest of each of the last seven days (at most ten): a run of saves cannot push the older, known-good copies out. They are a safety net; the apps do not show or ask about them.

To set up a device from Yaparr Cloud, choose **Restore from Yaparr Cloud** on the first-start screen of a computer with no account (or **From Yaparr Cloud** on a phone): check the address (filled in as above) and type the Sync key, sign in in the browser, and your account is fetched, unlocked and restored. The newest copy is used; if it cannot be opened (damaged), the next older one is tried by itself, with nothing to choose. That device becomes another device of your account, stays signed in and keeps the key.

If the account already holds data from another device, a device that has no key yet asks for that device's Sync key (and checks that it opens what the server holds) so that one key opens everything. If it has been lost, you can start a new key from the same question, and the older data then cannot be opened.

## The server a release is made for

`yaparr.release.json` sits beside the program in every release (the Windows installer, the Flatpak and the `.deb` are all made from the same published folder) and holds what that release is offered with:

```json
{ "backupServer": "https://cloud.yaparr.com" }
```

It is only a default for the address box: it is read, never written, a missing or unreadable file just means there is no default, and nothing is sent anywhere until you sign in. A person's own choice always wins, so anyone can point Yaparr at another server, and anyone who builds Yaparr themselves can change the file (the source is `apps/Yaparr.Desktop/yaparr.release.json`) or leave the address empty. The address has to be `https://` (`http://` only for a server on the same computer); anything else in the file is ignored.

## What the server can and cannot see

There are two secrets, and they do different jobs:

- **Your password signs you in.** It is typed into the server's page in your browser, so the server checks it (it keeps a hash). It gives access to the stored files, and can be changed or reset by whoever runs the server. It cannot open a backup.
- **The Sync key locks the backup.** The app makes it (120 random bits) and it never leaves your devices. The backup is locked with it before it is sent (`ProfileCipher`: Argon2id and AES-GCM, see [devices-and-accounts.md](devices-and-accounts.md)), so the server stores a file it cannot read, and refuses one that is not locked.

So nobody, the server's owner included, can open a backup without the Sync key, and a lost code cannot be recovered. If you still have a working device you do not need the backup at all: **Link another device** moves the account across.

The code and the token that keeps a computer signed in are in `backup-server.json` in the profile folder, sealed like the other files with secrets in them. That file is not part of a backup. The address has to be `https://` (`http://` is accepted only for a server on the same computer).

## The protocol

### Signing in

The way programs on a computer sign in to web services (OAuth's authorization code with PKCE, RFC 7636 and RFC 8252, cut down):

1. The app listens on a port of its own computer, makes a random `verifier` and `state`, and opens the browser at
   `GET /login?redirect_uri=http://127.0.0.1:<port>/callback&code_challenge=<base64url SHA-256 of the verifier>&state=<state>&device=<a name to show>`.
2. The person signs in on that page. The server only ever sends the browser back to an address on the computer it is on (`127.0.0.1` or `[::1]`).
3. The browser arrives at `redirect_uri?code=<code>&state=<state>`. The code works once, for two minutes. The app ignores a request whose `state` is not its own.
4. The app trades it: `POST /v1/token` with `{"code":"…","verifier":"…"}` gives `{"token":"…","username":"…"}`. Without the verifier the code is useless, so something that saw the code pass by cannot use it.

In these answers `username` is the account's name, which is its owner's email address. **Signing in from the sign-up page** uses a weaker, one-time handoff, because there the browser starts it and the app has no secret of its own to prove it with. The button is a link, `yaparr://backup-signin/<code>/<the server, escaped>` (only in this app form: there is no page on yaparr.com for it). The app shows the server, asks, and then trades the code: `POST /v1/token/handoff` with `{"code":"…"}` gives `{"token":"…","username":"…"}`. The code is 256 random bits, works once and for five minutes, and is made only for the account whose password has just been chosen. Another program on the same computer that also claims `yaparr://` links could take it, which is why the app always asks first, and why the code only ever gives a token for that account's locked backups. The link is refused if the server is not `https://` (or `http://` on the same computer), or if the code is not the shape the server makes.

Every later request carries `Authorization: Bearer <token>`. `DELETE /v1/token` signs the device out. `GET /account/password` is the page where a person changes their password, which signs every device out.

### Backups

| | | |
|---|---|---|
| `PUT /v1/backup` | body: the locked backup; `If-Match: "n"` = the newest version the client knows (`"0"` for none) | `201 {version,size}`; `409 {version}` if a newer one exists; `428` without `If-Match`; `413`; `400` if it is not a locked backup; `429` |
| `GET /v1/backup[?version=n]` | | the backup; header `X-Yaparr-Version` |
| `GET /v1/backup/versions` | | `{username, versions:[{version,size,at}]}`, newest first |
| `DELETE /v1/backup` | | `204` |

Errors are `{"error":"…"}` in words fit for the person; `401` means the device is not signed in (any more), and the app then shows the sign-in again. `If-Match` keeps two computers from overwriting each other's backup, and makes a request sent twice a `409` instead of a second copy. The app asks the server what the newest version is, and if it is not the one this computer last saved or restored, asks before adding another.
