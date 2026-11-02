# Security

[Documentation](README.md)

## Security

Identity is an ECDSA P-256 key; `PeerId` = SHA-256 of the public key. The hello carries an ephemeral
ECDH key signed by the identity key; session keys come from HKDF and traffic is AES-256-GCM with replay protection.
**Rooms** (`JoinRoomAsync(RoomKey)`) are invite-only: a room is a random 256-bit key. The DHT topic is derived from it,
so outsiders can't find the room or its members, and each connection proves key knowledge with an HMAC bound to that
connection's session and the prover's id (not replayable or reflectable, and it reveals nothing to non-members).
Peers without the key never become members, receive introductions or see messages. Share `key.ToString()` out of
band. Anyone holding the key is in, and removing one person means making a new key. There are no open rooms that
anyone can join by name; versions before 1.4 had them, and those rooms are not found by this version.

## Connections and replays

A hello is a signed statement that a temporary key belongs to its author. It proves who the key is for, not that the sender holds it, and anyone who ever connected to the author has a copy that can be repeated. So a hello only makes a candidate session. The connection counts as made, or the session is replaced (a peer that restarted), once a frame arrives that is sealed with that candidate's keys, which only the holder of the temporary key can make. Candidates that are not confirmed disappear after eight seconds, connections started by someone else wait in a room of 64 (at most 4, and 30 new ones in ten seconds, per address), and a repeated hello does not count as having heard from the other end.

## Secrets on disk

On Windows the identity, settings (private room keys), account settings, the WireGuard config, the TURN password and the history key are sealed with DPAPI, so only that Windows user on that PC can read them. Room and direct message history, and the pictures, GIFs and sounds, are encrypted with AES-GCM under that key (each line or 64 KB piece is tied to its file and place, so pieces cannot be moved, swapped, cut off or changed; files from earlier versions are sealed at the next start). Profile export writes the sealed files unsealed (with the history key) so a backup opens elsewhere, and importing seals them again, so keep the export zip safe.

## Limits on what a peer can do

Each connection has allowances for frames, bytes, messages, sound plays and sound lists, and a peer that keeps going past them is disconnected (`PeerLimits`). A reliable message is capped at 4 MB, a connection at 16 channels, 256 connections are kept, and silent connections are closed.

Updates are checked before they are installed; see [Installer and updates](installer-and-updates.md). What is still open is in the [backlog](../BACKLOG.md).
