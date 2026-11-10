# Backup server

A backup server keeps a **locked** copy of your profile backup, so you can bring your account back on any computer. It is optional: Yaparr works the same without one, and nothing is sent anywhere unless you use it. Whoever runs a server gives you its address and an account on it.

## Using it

**Settings > Backup > Backup server** on the desktop (Android does not have it yet):

1. Type the server's address and choose **Sign in with your browser**. Your browser opens the server's sign-in page; type your email address and password there (an account is named by its owner's email address, which the person running the server enters when they make it; nothing is ever sent to it). Yaparr never sees them. When you are signed in the browser says so and Yaparr shows "Signed in to … as …".
2. **Back up now** saves a backup. The first time, Yaparr makes a **recovery code** (like `K7QD-M3XF-9PLR-2WTE-HV8N-C4YB`) and shows it once: save it in a password manager or on paper. After that, backing up needs nothing typed.
3. **Show recovery code** shows it again on a computer that has it. **Change password** opens the server's page for that. **Sign out** makes the server forget this computer; the backups stay.

To bring a backup back, choose **Restore from a server** on the first-start screen of a computer with no account: type the address and the recovery code, sign in in the browser, and the newest backup is fetched, unlocked and restored. That computer becomes another device of your account, stays signed in and keeps the code.

If the account already has a backup made on another device, a computer that has no code yet asks for that device's code (and checks that it opens the backup) so that one code opens them all; you can also make a new one, and older backups then still need the old code.

## What the server can and cannot see

There are two secrets, and they do different jobs:

- **Your password signs you in.** It is typed into the server's page in your browser, so the server checks it (it keeps a hash). It gives access to the stored files, and can be changed or reset by whoever runs the server. It cannot open a backup.
- **The recovery code locks the backup.** The app makes it (120 random bits) and it never leaves your devices. The backup is locked with it before it is sent (`ProfileCipher`: Argon2id and AES-GCM, see [devices-and-accounts.md](devices-and-accounts.md)), so the server stores a file it cannot read, and refuses one that is not locked.

So nobody, the server's owner included, can open a backup without the recovery code, and a lost code cannot be recovered. If you still have a working device you do not need the backup at all: **Link another device** moves the account across.

The code and the token that keeps a computer signed in are in `backup-server.json` in the profile folder, sealed like the other files with secrets in them. That file is not part of a backup. The address has to be `https://` (`http://` is accepted only for a server on the same computer).

## The protocol

### Signing in

The way programs on a computer sign in to web services (OAuth's authorization code with PKCE, RFC 7636 and RFC 8252, cut down):

1. The app listens on a port of its own computer, makes a random `verifier` and `state`, and opens the browser at
   `GET /login?redirect_uri=http://127.0.0.1:<port>/callback&code_challenge=<base64url SHA-256 of the verifier>&state=<state>&device=<a name to show>`.
2. The person signs in on that page. The server only ever sends the browser back to an address on the computer it is on (`127.0.0.1` or `[::1]`).
3. The browser arrives at `redirect_uri?code=<code>&state=<state>`. The code works once, for two minutes. The app ignores a request whose `state` is not its own.
4. The app trades it: `POST /v1/token` with `{"code":"…","verifier":"…"}` gives `{"token":"…","username":"…"}`. Without the verifier the code is useless, so something that saw the code pass by cannot use it.

In these answers `username` is the account's name, which is its owner's email address. Every later request carries `Authorization: Bearer <token>`. `DELETE /v1/token` signs the device out. `GET /account/password` is the page where a person changes their password, which signs every device out.

### Backups

| | | |
|---|---|---|
| `PUT /v1/backup` | body: the locked backup; `If-Match: "n"` = the newest version the client knows (`"0"` for none) | `201 {version,size}`; `409 {version}` if a newer one exists; `428` without `If-Match`; `413`; `400` if it is not a locked backup; `429` |
| `GET /v1/backup[?version=n]` | | the backup; header `X-Yaparr-Version` |
| `GET /v1/backup/versions` | | `{username, versions:[{version,size,at}]}`, newest first |
| `DELETE /v1/backup` | | `204` |

Errors are `{"error":"…"}` in words fit for the person; `401` means the device is not signed in (any more), and the app then shows the sign-in again. `If-Match` keeps two computers from overwriting each other's backup, and makes a request sent twice a `409` instead of a second copy. The app asks the server what the newest version is, and if it is not the one this computer last saved or restored, asks before adding another.
