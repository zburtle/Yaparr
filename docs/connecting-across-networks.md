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

## Built-in WireGuard VPN

Yaparr contains its own WireGuard client (Noise IKpsk2, ChaCha20-Poly1305, BLAKE2s). In the Desktop app use
**Settings > Network > VPN** to import a standard `.conf` file and connect. Nothing is installed on the machine and no admin
rights are needed: only Yaparr's own UDP traffic is wrapped and sent to the VPN server, and every other program keeps
using the normal connection. Traffic to local-network addresses stays direct. While the VPN is enabled but down,
Yaparr's internet traffic is dropped instead of leaking out directly.

Peers then see the VPN server's address, so a symmetric NAT on your side stops mattering as long as the server's
outbound mapping is stable (a plain Linux WireGuard server with masquerading is). The NAT check is re-run through the
tunnel and shown in the status bar. The config, including its private key, is saved as `vpn.conf` in the profile
folder and is therefore part of a backup. Only IPv4 servers and a single peer are supported; the client does
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
