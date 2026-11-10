# Backup server

A backup server keeps a **locked** copy of your profile backup, so you can bring your account back on any computer. It is optional: Yaparr works the same without one, and nothing is sent anywhere unless you use it. Whoever runs a server gives you its address and an account on it.

On the desktop, **Settings > Backup > Backup server** is a small form: the server address, your username and your password, then **Back up now**. Below it, **Change my password on the server** opens its own labelled fields (current password, new password twice) and uses the address and username above. The address and username are remembered; the password never is. On the first-start screen of a computer with no account, **Restore from a server** fetches the newest one. (Android does not have it yet.)

## What the server can and cannot see

You type the server's address, your username and your password. The password never leaves your computer:

- It is turned into a **signing key** (`BackupServerClient`: Argon2id with 64 MiB and three passes, the username as the salt, then HKDF). The app signs every request with it, and the server holds only the matching public key, so it can check a signature but cannot sign and cannot learn the password.
- The same password **locks the backup** on your computer before it is sent (`ProfileCipher`: Argon2id and AES-GCM, with a salt of its own, see [devices-and-accounts.md](devices-and-accounts.md)). The server stores a file it cannot read, and refuses one that is not locked.

So there is no password reset: nobody, the server's owner included, can open a backup without the password, and a forgotten one cannot be recovered. A password needs 12 characters or more. A username is 3 to 32 letters, digits, dots, dashes and underscores, kept in lower case, and cannot be changed (it is part of the key).

The address has to be `https://` (`http://` is accepted only for a server on the same computer).

## The protocol

Every request carries four headers:

```
X-Yaparr-User            the username
X-Yaparr-Time            seconds since 1970 when the request was made (the server allows five minutes either way)
X-Yaparr-Content-Sha256  hex SHA-256 of the body (of nothing, for a request without one)
X-Yaparr-Sig             base64url Ed25519 signature of the text below
```

```
YAPSVC2 \n username \n METHOD \n path and query \n time \n content hash
```

| | | |
|---|---|---|
| `PUT /v1/backup` | body: the locked backup; `If-Match: "n"` = the newest version the client knows (`"0"` for none) | `201 {version,size}`; `409 {version}` if a newer one exists; `428` without `If-Match`; `413`; `400`; `429` |
| `GET /v1/backup[?version=n]` | | the backup; header `X-Yaparr-Version` |
| `GET /v1/backup/versions` | | `[{version,size,at}]`, newest first |
| `DELETE /v1/backup` | | `204` |
| `PUT /v1/account/key` | `{"publicKey":"<base64url, 32 bytes>"}`, signed with the current key | `204`; a changed password |

Errors are `{"error":"…"}` in words fit for the person. An unknown username and a wrong password get the same `401`, so the answer does not say who has an account. `If-Match` keeps two computers from overwriting each other's backup, and makes a request sent twice a `409` instead of a second copy.

The app asks the server what the newest version is, and if it is not the one this computer last saved or restored, asks before adding another.
