# Yaparr documentation

Yaparr is a serverless chat, voice and screen sharing library for .NET with a desktop app. These pages describe how it works and how to use it. What is still to do is in the [backlog](../BACKLOG.md); the [main README](../README.md) is the short introduction.

## Using Yaparr

- [Getting started](getting-started.md): use the library from code and try the command line.
- [The desktop app](desktop-app.md): the chat app, its notifications, diagnostics and logs.
- [Installer and updates](installer-and-updates.md): the MSI, the .deb and how the app updates itself.
- [Devices and accounts](devices-and-accounts.md): changing your name, linking devices, backing up.

## Features

- [Rooms and messages](rooms-and-messages.md): forwarding, connection limits, presence, catch-up and delivery.
- [Pictures and GIFs](pictures-and-gifs.md)
- [Voice channels](voice.md)
- [Soundboard](soundboard.md)
- [Sharing a screen](screen-sharing.md)
- [Moderation and blocking](moderation.md): blocks, owners, admins, bans, replacing the room secret and channels.

## How it works

- [How it works](architecture.md): the layers, hole punching, address records and channels.
- [Connecting across networks](connecting-across-networks.md): port mapping, STUN, TURN (including sharing a relay) and the built-in VPN.
- [Relaying](relaying.md): how members carry connections for each other.
- [Streams and large rooms](streams-and-large-rooms.md): trees for video and big voice channels.
- [Security](security.md): identities, encryption, invite-only rooms and secrets on disk.
- [Known limitations](known-limitations.md)

## Working on Yaparr

- [Development](development.md): layout, tests, network tests and releases.
