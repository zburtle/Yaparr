# Relaying

[Documentation](README.md)

If two room members can't punch through to each other (typically both behind symmetric NATs) but a
third member can reach both, then after `RelayFallbackDelay` (8 s) they open a connection *through* that member.
The whole connection (handshake, identity check, encryption, reliable channels) still runs end to end between the
two peers; the relay only forwards opaque datagrams and cannot read or alter them. It only forwards between two
members of a room it shares with both, and only over direct connections (no relay chains). Both sides keep
trying direct, and a direct path replaces a relayed one as soon as it works. `PeerInfo.IsRelayed` tells you which
you have. Relayed paths add latency and spend the relay's bandwidth. Disable with `EnableRelaying = false`.

Who relays is not a choice. A node relays when its network allows it (a public address, an open port mapping, or a
router that keeps the same public port for every destination: `ChatNode.IsRelayCapable`) and refuses otherwise,
so members behind strict routers never carry anyone's traffic. Members announce this to the people they are
connected to (`Rly` in the hello, and a `caps` message when it changes), and a peer only asks members that can
relay. A relay's work is capped (`PeerNetworkOptions.RelayBytesPerSecond`, `RelayBytesPerSender` and
`MaxRelayedPairs`, about 2 Mbit/s in total by default) and `ChatNode.RelayStats` shows what it has passed on. If a
connection ends up on the shared TURN server and a capable member can carry it instead, it moves to that member
and the server is left alone.

Someone you reach through a member also keeps a **standby path** through a second capable member (opened a few
seconds after the first, and refreshed with the same 4 s keep-alives). If the member in use disappears, the standby
takes over at once: same person, same state, no new handshake and no disconnect/connect events
(`PeerNetwork.ConnectionPromoted`); anything not yet confirmed is sent again on it. Room references depend on the
connection's session, so both sessions are understood while the switch settles. A direct connection, when one works,
replaces a relayed one and its standby is closed.

If the member carrying a connection leaves and there is no standby, both ends move to another capable member
straight away, without the usual `RelayFallbackDelay`. Any member who says it is connected to the other person can take over, not only the one
who introduced them. A member that leaves without saying goodbye is noticed within about 15 s on a relayed path
(4 s keep-alives, 15 s idle limit, against 10 s and 35 s on direct ones). Messages not yet confirmed are sent again
on the new path.

## How connections end up

`ChatNode.GetConnectionPaths()` says how the people you are connected to are reached right now (directly, through a member, or through the shared TURN server) and counts, since the node started, the connections made each way, the connections moved off the shared server onto a member, and the times a standby path took over from one that was lost. They are the numbers to look at to see how much load the members take off the server, and what a standby for every connection would have cost.

The network check (Settings > Network > Check my connection…) shows them under "Connected to N people", in "Some connections use the shared server" (only when there are any, with a note that its bandwidth is limited) and "Connections since Yaparr started"; the relay counters of the member relay ("You help others connect") are in the same list. Settings > Updates & diagnostics > Copy diagnostics has the same figures in one line.

