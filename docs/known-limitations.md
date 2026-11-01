# Known limitations

[Documentation](README.md)

- IPv4 only; a flat routing table instead of k-buckets; DHT node is read-only (BEP 43) by default.
- Connections are capped (see above), but this has only been tested with 24 simulated nodes on one machine, so larger rooms are unproven. Forwarding is best effort: if the neighbour graph splits, the halves only exchange messages once a connection or catch-up bridges them. Members report their connections and degrees themselves, so a malicious member could claim phantom neighbours, suppress forwarding, or trigger drops by announcing a tiny degree. Presence traffic grows with room size (one beacon per member per interval, sent to each neighbour).
- Catch-up needs someone online who holds the messages (no server stores them); members can only vouch for messages by signature, so a malicious member could inject messages from invented identities, but not from real members. No offline delivery for 1:1 chats. Relaying needs a mutual room member with a good connection who is directly reachable from both sides; there are no dedicated relay servers and no relay chains, and small rooms (a pair, or nobody with an open router) fall back on the TURN server.
- Hole punching is verified only on loopback in tests; real-NAT behaviour should be tried across networks.
