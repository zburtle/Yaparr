# Yaparr

A .NET library for serverless chat. Peers find each other on the **BitTorrent mainline DHT**, then open
**direct, encrypted UDP connections** using **hole punching** (no manual port forwarding). Supports one-to-one
chats and group rooms, and is structured so voice chat can be added later as another channel.

```csharp
using var node = new ChatNode(new ChatNodeOptions { DisplayName = "zach" });
node.Start();
Console.WriteLine(node.Id);                       // share this id; others can call you by it

var room = await node.JoinRoomAsync("my-room");   // group chat: anyone using the same name joins
room.MessageReceived += m => Console.WriteLine($"{m.From.DisplayName}: {m.Text}");
await room.SendAsync("hello");

var secret = await node.JoinPrivateRoomAsync(RoomKey.Generate());   // invite-only; share key.ToString()

var dm = await node.OpenDirectChatAsync(new PeerId("…"));   // one-to-one by peer id
await dm.SendAsync("hi");
```

Try it: `dotnet run --project samples/Yaparr.Cli -- alice room lobby` in two terminals/machines.
Run tests: `dotnet test`.

**Desktop UI.** `samples/Yaparr.Wpf` is a very basic WPF chat window (Windows): a list of rooms and direct chats,
messages, members, and buttons to join a public room, create or join a private room (with a copy-invite button) or
open a direct chat by peer ID. Your name, identity key, joined rooms and message history are kept in
`%LOCALAPPDATA%\\Yaparr` so you keep the same peer ID and rejoin your rooms on the next start. To run two copies on
one machine, give each its own folder: `Yaparr.Wpf.exe --name alice --data C:\\temp\\alice` (the launch profiles
`alice` and `bob` do this). Run it with `dotnet run --project samples/Yaparr.Wpf`.

**Installer.** `.\installer\build.ps1` publishes the WPF app self-contained (no .NET install needed on the target
machine) and wraps it in `artifacts\Yaparr-<version>-x64.msi` (about 48 MB) using WiX 5, which is restored as a local
dotnet tool. The MSI installs per user to `%LOCALAPPDATA%\Programs\Yaparr` by default with a Start menu shortcut and needs no
administrator rights. It is a single file: double-clicking opens a wizard (welcome, install folder, progress, finish
with a "Launch Yaparr" checkbox), and `msiexec /i Yaparr-1.0.0-x64.msi /qn` installs it silently. Installing a newer version upgrades in place and uninstalling leaves your chats and identity in
`%LOCALAPPDATA%\Yaparr`. Set the version in `samples/Yaparr.Wpf/Yaparr.Wpf.csproj` (or pass `-Version`). The
installer is unsigned, so Windows SmartScreen will warn on first run, and Windows Firewall will ask to allow the app
(allow private networks for LAN discovery; allow public for internet peers).

**Debugging the CLI.** `samples/Yaparr.Cli/Properties/launchSettings.json` has profiles for Visual Studio / Rider
(or `dotnet run --launch-profile "<name>"`): alice/bob/carol in a shared local room, two in a local private room,
a "wait for a DM" instance, and one on the real DHT. The local ones pass `--local`, which swaps the DHT for a
temp-directory discovery so instances on one machine find each other in seconds. Start one profile at a time,
e.g. from separate terminals. `--history` keeps each name's group history on disk under the temp directory: run
alice and bob, stop bob, chat as alice, then start bob again to see the `missed` messages arrive.

## How it works

| Layer | Namespace | Role |
|---|---|---|
| Discovery | `Discovery`, `Discovery.Dht` | `IPeerDiscovery` (announce / find by 20-byte topic). Default `DhtDiscovery` is a BEP 5 client: KRPC, iterative `get_peers`, `announce_peer` with `implied_port`. Topics are SHA-1 of a room name (rooms) or a peer id (1:1 lookup). |
| Transport | `Transport` | `UdpMux` – one UDP socket shared by DHT and peers, so the NAT-mapped port the DHT records is the one peers punch to. `PeerConnection` – hole punch, handshake, encryption, channels. `PeerNetwork` – connection routing, accepts inbound punches, channel dispatch. |
| Chat | `Chat` | `ChatNode`, `ChatRoom`, `DirectChat`. JSON messages on channel 1. |

**Hole punching.** After discovery, both sides send signed hello packets to each other's public endpoint every
300 ms (up to 45 s). Each outbound packet opens the sender's NAT, so the peer's packets get through. Group members
also *introduce* newcomers to each other over existing connections so mesh links punch simultaneously.
Works for the common cone/endpoint-independent NATs.

**Same network.** The DHT only reports a peer's *public* address, and most home routers cannot loop traffic back
to a device on their own LAN (no NAT hairpinning), so two peers behind one router would never connect that way.
By default the node therefore also runs BEP 14 Local Service Discovery (multicast, `LocalServiceDiscovery`) and
combines it with the DHT (`CompositeDiscovery`); same-LAN peers find each other's LAN addresses directly. If a peer
is reachable over several paths (LAN, VPN, public) the two ends deterministically keep one connection. Turn it off
with `EnableLocalDiscovery = false`. Firewalls must allow UDP multicast on port 6771 for this.

**Relaying.** If two room members can't punch through to each other (typically both behind symmetric NATs) but a
third member can reach both, then after `RelayFallbackDelay` (8 s) they open a connection *through* that member.
The whole connection (handshake, identity check, encryption, reliable channels) still runs end to end between the
two peers; the relay only forwards opaque datagrams and cannot read or alter them. It only forwards between two
members of a room it shares with both, and only over direct connections (no relay chains). Both sides keep
trying direct, and a direct path replaces a relayed one as soon as it works. `PeerInfo.IsRelayed` tells you which
you have. Relayed paths add latency and spend the relay's bandwidth. Disable with `EnableRelaying = false`.

**Message forwarding.** Group chat no longer needs every pair of members to be connected. A live group message is
signed by its author, so any member can pass it on without being able to alter or forge it. When a member receives a
new message it forwards it to the room members it is connected to, except the one it came from, the author, and
anyone who is already known to be connected to either of those (members tell each other which room members they are
directly connected to, which also lets `room.Members` list members you only reach through others, with
`IsRelayed = true`). Duplicates are dropped by message id, so loops end. Forwarding only ever goes to members of that
room, who proved they hold the key: a node that is not in the group never carries or sees the room's messages (it
can still forward opaque encrypted datagrams for a connection-level relay only if it shares a room with both ends,
see above). Messages are forwarded one hop at a time, so they travel any number of member-to-member hops.

**Connection limits.** A node does not connect to every member of a room. It aims for `TargetConnections` (8)
neighbours per room and never keeps more than `MaxConnections` (12) in total; set `TargetConnections = 0` for the old
full mesh. Peers found through the DHT or learned from other members are kept as candidates and dialled at random
only while below the target. Members periodically swap small random samples of who they are connected to, so
candidates stay fresh, and a newcomer is only introduced to members that still have room. A node at its maximum
still accepts a newcomer that has almost no connections (two or fewer) by dropping one of its better-connected
neighbours, and refuses anyone else; dropped peers are not redialled for a minute. Connections you opened on purpose
(`ConnectAsync`, `OpenDirectChatAsync`, an open direct chat) are never dropped. Messages reach everyone by
forwarding (above), and catch-up repairs what forwarding misses.

**Presence.** Because a node only sees its neighbours, `room.Members` also lists members it has heard about through
signed presence beacons: every `PresenceInterval` (30 s) each member signs "I'm here" for the room and neighbours
pass it on, and a member who leaves sends a signed "gone". A member counts as online for three intervals after their
last beacon (so a crash takes up to 90 s to show), and beacons from non-members are rejected.

**Security.** Identity is an ECDSA P-256 key; `PeerId` = SHA-256 of the public key. The hello carries an ephemeral
ECDH key signed by the identity key; session keys come from HKDF and traffic is AES-256-GCM with replay protection.
**Public rooms** (`JoinRoomAsync(name)`) are open: anyone who knows the name can join. **Private rooms**
(`JoinPrivateRoomAsync(RoomKey)`) use a random 256-bit key instead: the DHT topic is derived from it, so outsiders
can't find the room or its members, and each connection proves key knowledge with an HMAC bound to that
connection's session and the prover's id (not replayable or reflectable, and it reveals nothing to non-members).
Peers without the key never become members, receive introductions or see messages. Share `key.ToString()` out of
band. Anyone holding the key is in, and removing one person means making a new key.

**Catch-up.** Group messages are signed by their author (ECDSA, bound to the room) and kept in a bounded history
(500 messages / 7 days by default, in memory or persisted with `HistoryStore = new FileHistoryStore(dir)`). When two
members connect, each sends the keys of the messages it holds and the other replies with the ones missing, so a
member who was offline gets what they missed from whoever was online, and anyone can pass on a message without
being able to forge or alter it. Catch-up messages arrive via `MessageReceived` with `IsCatchUp = true` (sort by
`Timestamp`); `room.GetHistory()` returns everything held. Newcomers also receive recent history; set
`SyncHistory = false` to disable. One-to-one chats are not synced. Stored history is plain text on disk.

**Channels (for voice etc.).** Each connection carries numbered channels, each usable as reliable/ordered
(`SendReliable`, any size) or unreliable (`SendUnreliable`, ≤1100 bytes, ideal for audio frames). A voice module would:

```csharp
node.Network.RegisterChannel(ChannelIds.Voice, (conn, ch, data, reliable) => { /* decode audio */ });
node.Network.Get(peerId)!.SendUnreliable(ChannelIds.Voice, opusFrame);
```

and use the chat channel (or its own reliable channel) for call signalling. The same connections serve chat and voice.

## Connecting across networks

Peers behind home routers have to punch through their NATs. Yaparr does this in layers, cheapest first:

1. **Port mapping.** On start the node asks the router for a UDP port through NAT-PMP, then UPnP. If that works the
   mapped port is announced to the DHT and anyone can reach you.
2. **STUN.** The node asks public STUN servers what address and port they see, from the same socket the DHT uses. If
   two servers see different ports the NAT is symmetric, and direct connections to strangers are unlikely. The result
   is shown in the status bar.
3. **TURN.** If the NAT is symmetric (or `always` is set) and a TURN server is configured, the node allocates a relay
   address, announces that address to the DHT through the relay, and mirrors handshake packets through it. Data
   switches to the relay only for peers that cannot be reached directly. A TURN server is the one piece of
   infrastructure involved; it forwards encrypted packets and cannot read them.

Settings live in `network.json` in the profile folder (all keys optional):

```json
{
  "portMapping": true,
  "stun": ["stun.l.google.com:19302", "stun.cloudflare.com:3478"],
  "turn": { "server": "turn.example.com:3478", "username": "user", "credential": "secret", "always": false }
}
```

Group members still relay for each other once connected, so a TURN server mainly helps the very first connection.

## Known limitations

- IPv4 only; a flat routing table instead of k-buckets; DHT node is read-only (BEP 43) by default.
- Connections are capped (see above), but this has only been tested with 24 simulated nodes on one machine, so larger rooms are unproven. Forwarding is best effort: if the neighbour graph splits, the halves only exchange messages once a connection or catch-up bridges them. Members report their connections and degrees themselves, so a malicious member could claim phantom neighbours, suppress forwarding, or trigger drops by announcing a tiny degree. Presence traffic grows with room size (one beacon per member per interval, sent to each neighbour).
- Catch-up needs someone online who holds the messages (no server stores them); members can only vouch for messages by signature, so a malicious member could inject messages from invented identities, but not from real members. No offline delivery for 1:1 chats. Relaying needs a mutual room member who is directly reachable; there are no dedicated relay servers, no relay bandwidth limits, and no relay chains.
- Hole punching is verified only on loopback in tests; real-NAT behaviour should be tried across networks.
