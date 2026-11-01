# Connecting across networks

[Documentation](README.md)

Peers behind home routers have to punch through their NATs. Yaparr does this in layers, cheapest first:

1. **Port mapping.** On start the node asks the router for a UDP port through NAT-PMP, then UPnP. If that works the
   mapped port is announced to the DHT and anyone can reach you.
2. **STUN.** The node asks public STUN servers what address and port they see, from the same socket the DHT uses. If
   two servers see different ports the NAT is symmetric, and direct connections to strangers are unlikely. The result
   is shown in the status bar.
3. **TURN (off by default).** The code is in place but disabled; turn it on from **Network > TURN relay…** in the Desktop app (or set `"enabled": true` in the `turn` block of `network.json`). When enabled, if the NAT is symmetric (or `always` is set), the node allocates a relay
   address, announces that address to the DHT through the relay, and mirrors handshake packets through it. Data
   switches to the relay only for peers that cannot be reached directly. A TURN server is the one piece of
   infrastructure involved; it forwards encrypted packets and cannot read them.

## Built-in WireGuard VPN

Yaparr contains its own WireGuard client (Noise IKpsk2, ChaCha20-Poly1305, BLAKE2s). In the Desktop app use
**Network > VPN…** to import a standard `.conf` file and connect. Nothing is installed on the machine and no admin
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
