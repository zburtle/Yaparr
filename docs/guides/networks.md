# Connecting through difficult networks

Yaparr has no company server. Your computer connects straight to the people in your rooms, and most of the time that just works. Some home and work networks make that hard, usually because the router is strict. If people cannot reach you, the connection check in Settings, under Network, tells you why, and there are three ways to help.

## WireGuard VPN

Connect Yaparr to a WireGuard VPN you already pay for, to get around networks that block direct connections. Only Yaparr's own traffic goes through the VPN; other programs are not affected, and people can reach you through the VPN's address.

## TURN relay

A TURN server relays Yaparr's traffic when two peers cannot connect directly (for example behind symmetric NAT). Messages stay end-to-end signed and encrypted between you and the other person; the relay only forwards packets. Yaparr has no relay of its own: run one yourself, or import one that a friend shares with you.

The relay code holds the relay's address, user name and password. Anyone who has it can use your relay, and the bandwidth is yours, so share it only with people you trust.

## Yaparr Cloud

If your network only lets web traffic out, [Yaparr Cloud](yaparr-cloud.md#when-your-network-blocks-udp) can pass Yaparr's traffic on for messages and voice.

See [Connecting across networks](../connecting-across-networks.md) for how each works.
