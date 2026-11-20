# Connecting across networks

[Documentation](README.md)

Peers behind home routers have to punch through their NATs. Yaparr does this in layers, cheapest first:

1. **Port mapping.** On start the node asks the router for a UDP port through NAT-PMP, then UPnP. If that works the
   mapped port is announced to the DHT and anyone can reach you.
2. **STUN.** The node asks public STUN servers what address and port they see, from the same socket the DHT uses. If
   two servers see different ports the NAT is symmetric, and direct connections to strangers are unlikely. The result
   is shown in the status bar.
   One answer is enough to know your public address, so the connection check only says "Cannot reach the internet" when no server answered at all. Some systems refuse to say what the router or the interfaces are (Android raises `PlatformNotSupportedException` for the gateway of an interface); that only means no port mapping is tried, it does not stop this step, and when looking at the network fails for another reason the check says "Could not look at the network" with the reason rather than blaming the internet.
3. **TURN (your own relay, off until you set one up).** Yaparr ships with no relay and no relay password. Set up a TURN server of your own (any will do; [coturn](https://github.com/coturn/coturn) is a common one), or import one that someone shared with you, under **Settings > Network > TURN relay** in the Desktop app (or fill in the `turn` block of `network.json` and set `"enabled": true`). When enabled, if the NAT is symmetric (or `always` is set), the node allocates a relay
   address, announces that address to the DHT through the relay, and mirrors handshake packets through it. Data
   switches to the relay only for peers that cannot be reached directly. A TURN server is the one piece of
   infrastructure involved; it forwards encrypted packets and cannot read them.

### Networks that block UDP (Yaparr Cloud gateway)

Everything between devices (finding people, connecting, the chat protocol) is UDP, and some networks (workplaces, schools, hotels) let only web traffic out. Without help, a device there cannot find anyone or be found. If the device is signed in to a Yaparr Cloud server whose operator has the **gateway** on (see the Yaparr.Cloud README, "UDP gateway"), Yaparr sends its UDP traffic to the server inside a WebSocket on port 443, and the server sends it from a UDP socket of its own and hands back what arrives. Only the blocked device needs it; the people it talks to do not know, and see the server's address.

- **When.** After a check of the network in which not one STUN server answered and nothing else carries the traffic (`ChatNode.UdpBlocked`), `GatewayFallback` connects the gateway, then looks at the network again through it. If an address comes back it stays; if the server has the gateway off, the sign-in is no good, or nothing comes back, it is let go and not tried again for ten minutes (so a network that is simply offline is not tried over and over). When the network changes the gateway is let go first, so the new network is checked as it is, and it is offered again only if UDP is blocked there too. While it is in use the node also asks, every 30 seconds, whether UDP gets out of the network by itself again (a STUN request sent from the socket itself, past the gateway: `NatProbe.DirectUdpWorksAsync`); two answers in a row, so within about a minute, and the gateway is let go and the network looked at again without it (`ChatNodeOptions.GatewayProbeInterval`). A network that lets UDP through for a moment and then blocks it again is handled the same way as any other: the check that follows asks for the gateway again. A VPN set up in Yaparr always takes the place of the gateway.
- **Messages and voice, not screens.** The server caps it (about 196 kbit/s each way, 2 GiB a day for the account, small datagrams, public addresses only), so chat, presence, the lookups that find people and voice (about 90 kbit/s for each person you talk to) fit, and screen sharing, which needs several times that, does not. What the server holds back is told to the device (`GatewayLimited`) and logged. The apps also turn screens off while the gateway is in use (`ScreenGate`): the share and watch buttons are greyed with the reason as their tip, and a share or watch under way is ended.
- **Settings.** *Use Yaparr Cloud for messages when this network blocks UDP* in the Yaparr Cloud section of the settings (on by default; belongs to the device, not synced). The network check says when the gateway is in use.
- **Privacy.** The server sees that the account used the gateway, when and how much, and the IP addresses of the places its datagrams went to and came from. It never sees what is in them: the devices lock that between themselves.
- **How.** `GatewayTunnel` is an `ITunnel` like the WireGuard VPN (`UdpMux.Tunnel`): public addresses go through it, addresses on this network or machine are sent directly, and it reconnects by itself with a growing wait. `GatewayFrames` is the wire format, `WebSocketGatewayLink` the connection, and the pieces are tested against a link in memory (`GatewayTunnelTests`), including a node that finds an address through it by STUN.

### IPv6

Yaparr listens on both IPv4 and IPv6 with one socket when the system allows it. If your computer has a public IPv6 address, the node publishes it alongside its IPv4 addresses in the peer record (in separate entries with their own signature, so older versions still read the IPv4 part). IPv6 needs no address translation, so a peer with a public IPv6 address can be reached directly, provided a firewall lets the port through. Addresses that are only local (link-local, unique-local) are not published.

Not yet: IPv6 relay through a TURN server (IPv6 traffic always goes directly), IPv6 addresses in the DHT itself, and finding peers on the local network over IPv6.

## Built-in WireGuard VPN

Yaparr contains its own WireGuard client (Noise IKpsk2, ChaCha20-Poly1305, BLAKE2s). In the Desktop app use
**Settings > Network > VPN** to import a standard `.conf` file and connect. Nothing is installed on the machine and no admin
rights are needed: only Yaparr's own UDP traffic is wrapped and sent to the VPN server, and every other program keeps
using the normal connection. Traffic to local-network addresses stays direct. While the VPN is enabled but down,
Yaparr's internet traffic is dropped instead of leaking out directly.

Peers then see the VPN server's address, so a symmetric NAT on your side stops mattering as long as the server's
outbound mapping is stable (a plain Linux WireGuard server with masquerading is). The NAT check is re-run through the
tunnel and shown in the status bar. The config, including its private key, is saved as `vpn.conf` in the profile
folder and is therefore part of a backup. The server's address can be IPv4 or IPv6, and the interface can have an IPv4 address, an IPv6 address, or both. Traffic is carried over either family as long as the configuration has an address of that family; public traffic for a family the configuration does not cover is dropped. A single peer is supported, and the client does
not answer handshakes started by the server, so it relies on its own 2-minute rekey.

Settings live in `network.json` in the profile folder (all keys optional). The Desktop app looks for peers on the local network too unless `localDiscovery` is `false`:

```json
{
  "portMapping": true,
  "localDiscovery": true,
  "stun": ["stun.l.google.com:19302", "stun.cloudflare.com:3478"],
  "turn": { "server": "turn.example.com:3478", "username": "user", "credential": "secret", "enabled": false, "always": false }
}
```

Group members still relay for each other once connected, so a TURN server mainly helps the very first connection.

### Sharing your relay

If you run a TURN server, you can hand it to friends so their Yaparr uses it too. In **Settings > Network > TURN relay**, **Share this relay…** turns the server, user name and password into a code starting with `yaparr-relay:`, which you copy or save as a `.yrelay` file. The other person chooses **Import a relay…** and pastes the code or opens the file; the relay is filled in for them to look at, and nothing changes until they press **Save**. In the library the same thing is `RelayShare.Create` and `RelayShare.TryParse`.

The code holds the relay's password, so anyone who has it can use your relay and spend its bandwidth, and can pass it on. Send it only to people you trust, by a private route. The only way to withdraw it is to change the password on the server, which cuts off everyone who has it.
