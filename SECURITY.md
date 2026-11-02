# Security policy

## Reporting a vulnerability

Please report it privately through GitHub: open the **Security** tab of this repository and choose **Report a vulnerability**. Please do not open a public issue or post it in a chat room before there is a fix.

Include what you found, how to reproduce it and which version you tested. This is a one-person project, so I answer as quickly as I can: expect a first reply within about a week, and I will keep you posted until it is fixed. I am happy to credit you in the release notes unless you would rather not be named.

## Versions

Only the latest release gets fixes. Yaparr updates itself from the releases page, so that is where everyone ends up.

## What to know about it

Yaparr has not had an independent security review. The protocol, the encryption around it (`src/Yaparr/Transport`), the room proofs and the built-in WireGuard client (`src/Yaparr/Vpn`, which is experimental) are my own work on top of the .NET and BouncyCastle cryptography libraries. The design, what is and is not protected, and what is still open are described in [docs/security.md](docs/security.md) and the [backlog](BACKLOG.md). The installers are not code-signed, so updates are trusted through this repository's GitHub account.

## Looking for problems

Good-faith research is welcome. Please test against your own copies and peers, and do not attack other people's computers, the BitTorrent DHT or anyone's relay server. Findings I would like to hear about include: a way to read or forge messages, to join a room without its key, to get another member's device to run code, to make the app write outside its folder, or to cut a connection off from far away.
