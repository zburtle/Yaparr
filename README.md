<p align="center"><img src="assets/logo/yaparr-logo.svg" alt="Yaparr" width="420"></p>

# Yaparr

Serverless chat, voice calls and screen sharing for .NET, with a desktop app for Windows and Linux. Peers find each other on the **BitTorrent mainline DHT**, then open **direct, encrypted UDP connections** using **hole punching**, so there is no server to run and no port forwarding to set up. Website: [yaparr.com](https://yaparr.com).

- **Rooms and direct chats.** A room is a random 256-bit key you share with the people you want in; outsiders cannot find it or read it. Messages are signed, forwarded between members and caught up from whoever was online.
- **Voice channels and a soundboard** in every room, with echo cancellation and noise suppression, and trees that carry big calls.
- **Screen sharing** of a monitor or a window (Windows), watched on any system.
- **Pictures and GIFs**, checked and fetched from any member that has them.
- **Moderation.** Owners, admins, bans, text and voice channels, and replacing the room secret so a ban holds.
- **Several devices** for one person, linked with an encrypted code, with messages and settings kept in step.
- **Hard networks.** Port mapping, STUN, members relaying for each other, an optional TURN server of your own (shareable with friends as a code) and a built-in WireGuard client.

## The name

**Yaparr** (*noun*): from *yap*, to talk, and at length, plus *-arr*, borrowed from the family of self-hosted apps such as Sonarr and Radarr, whose names pair a pirate's "arr!" with sonar and radar.

The ending is a nod to shared roots and not a claim of kinship: Yaparr does not download or manage media. It does find its people the way torrent clients find each other, through the BitTorrent mainline DHT, and then talks to them directly with no server in the middle. Yap, in pirate.

## Try it

```csharp
using var node = new ChatNode(new ChatNodeOptions { DisplayName = "zach" });
node.Start();
Console.WriteLine(node.Id);                       // share this id; others can call you by it

var key = RoomKey.Generate();                     // a room is its key; share key.ToString() with the people you want in
var room = await node.JoinRoomAsync(key, "my room");
room.MessageReceived += m => Console.WriteLine($"{m.From.DisplayName}: {m.Text}");
await room.SendAsync("hello");

var dm = await node.OpenDirectChatAsync(new PeerId("…"));   // one-to-one by peer id
await dm.SendAsync("hi");
```

Try the command line: `dotnet run --project samples/Yaparr.Cli -- alice room` prints an invite; `dotnet run --project samples/Yaparr.Cli -- bob room <invite>` joins it, in two terminals or on two machines. Run the desktop app with `dotnet run --project samples/Yaparr.Desktop`, or install it from the [releases](https://github.com/zburtle/Yaparr/releases). Run the tests with `dotnet test`.

## Documentation

The [documentation](docs/README.md) is a small wiki, one page per topic:

- Using it: [Getting started](docs/getting-started.md), [The desktop app](docs/desktop-app.md), [Installer and updates](docs/installer-and-updates.md), [Devices and accounts](docs/devices-and-accounts.md).
- Features: [Rooms and messages](docs/rooms-and-messages.md), [Pictures and GIFs](docs/pictures-and-gifs.md), [Voice channels](docs/voice.md), [Soundboard](docs/soundboard.md), [Sharing a screen](docs/screen-sharing.md), [Moderation and blocking](docs/moderation.md).
- How it works: [How it works](docs/architecture.md), [Connecting across networks](docs/connecting-across-networks.md), [Relaying](docs/relaying.md), [Streams and large rooms](docs/streams-and-large-rooms.md), [Security](docs/security.md), [Known limitations](docs/known-limitations.md).
- Working on it: [Development](docs/development.md), including how releases are made.

What is still to do is in the [backlog](BACKLOG.md).

## Privacy and security

There is no account, no server of ours and no telemetry. Other members of a room see your IP address, because they connect to you directly, and so does the BitTorrent DHT; STUN servers (Google's and Cloudflare's by default, changeable) learn it too. The app checks GitHub for updates when it starts. What is protected, what is not and what the app contacts are in [docs/security.md](docs/security.md). Yaparr has not had an independent security review, and the built-in WireGuard client is experimental. To report a problem privately, see [SECURITY.md](SECURITY.md).

## Licence

Yaparr is free software under the [GNU GPL](COPYING), version 3 or later: the library, the apps and everything else. [LICENSE.txt](LICENSE.txt) has the details, including where to get the source of each release. Releases up to v1.10.0 were published under the MIT licence and stay available under it. Third-party software is listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
