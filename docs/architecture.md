# How it works

[Documentation](README.md)

| Layer | Namespace | Role |
|---|---|---|
| Discovery | `Discovery`, `Discovery.Dht` | `IPeerDiscovery` (announce / find by 20-byte topic). Default `DhtDiscovery` is a BEP 5 client: KRPC, iterative `get_peers`, `announce_peer` with `implied_port`. Topics are SHA-1 of a room name (rooms) or a peer id (1:1 lookup). |
| Transport | `Transport` | `UdpMux` – one UDP socket shared by DHT and peers, so the NAT-mapped port the DHT records is the one peers punch to. `PeerConnection` – hole punch, handshake, encryption, channels. `PeerNetwork` – connection routing, accepts inbound punches, channel dispatch. |
| Chat | `Chat` | `ChatNode`, `ChatRoom`, `DirectChat`. JSON messages on channel 1. |

## Hole punching

After discovery, both sides send signed hello packets to each other's public endpoint every
300 ms (up to 45 s). Each outbound packet opens the sender's NAT, so the peer's packets get through. Group members
also *introduce* newcomers to each other over existing connections so mesh links punch simultaneously.
Works for the common cone/endpoint-independent NATs.

## Address records (BEP 44)

Besides announcing under a room's topic, every node publishes a small signed record,
"my current addresses are X, Y, Z", as a BEP 44 mutable item (`PeerRecord`, `DhtNode.PutMutableAsync`). A newer record
replaces the older one instead of piling up beside it, so opening a direct chat or redialling a peer that moved asks
for exactly that peer's latest address (`IPeerRecords`). The DHT key is derived from the peer ID, so anyone who knows
the ID can overwrite the record; readers therefore trust only the inner signature made with the peer's real identity
key and ignore anything else. Records are republished when the addresses change and every 40 minutes. Add addresses
the node cannot discover itself (for example a forwarded port) with `ChatNodeOptions.AdvertisedEndPoints`.

## Same network

The DHT only reports a peer's *public* address, and most home routers cannot loop traffic back
to a device on their own LAN (no NAT hairpinning), so two peers behind one router would never connect that way.
By default the node therefore also runs BEP 14 Local Service Discovery (multicast, `LocalServiceDiscovery`) and
combines it with the DHT (`CompositeDiscovery`); same-LAN peers find each other's LAN addresses directly. If a peer
is reachable over several paths (LAN, VPN, public) the two ends deterministically keep one connection. Turn it off
with `EnableLocalDiscovery = false`. Firewalls must allow UDP multicast on port 6771 for this.

## Channels (for voice etc.)

Each connection carries numbered channels, each usable as reliable/ordered
(`SendReliable`, any size) or unreliable (`SendUnreliable`, ≤1100 bytes, ideal for audio frames). A voice module would:

```csharp
node.Network.RegisterChannel(ChannelIds.Voice, (conn, ch, data, reliable) => { /* decode audio */ });
node.Network.Get(peerId)!.SendUnreliable(ChannelIds.Voice, opusFrame);
```

and use the chat channel (or its own reliable channel) for call signalling. The same connections serve chat and voice.

## Finding peers quickly

The first lookup on the DHT used to dominate the time to connect. The node is ready as soon as 16 nodes are known instead of waiting for every seed to answer or time out (35 to 120 ms instead of 3.3 s with a node cache), and a node gets only as long as four round trips to answer (600 ms to 1.5 s, was always 1.5 s) with 10 queries at once (was 6). Measured against the real network, six cold starts each: median first search 3.1 and 4.3 s, against 5.7 to 6.9 s before. Still bounded by the network: about half of the nodes asked never answer.
