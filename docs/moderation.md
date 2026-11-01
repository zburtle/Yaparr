# Moderation and blocking

[Documentation](README.md)

`ChatNode.Block(peer)` is a personal block: it only changes what you see and hear. Their messages and pictures never
reach you (they are kept quietly so they are not asked for again, but not shown or downloaded), they leave your member
lists and your voice channel, your connection to them is closed and not opened again, and you stop relaying for them.
They are not told, and everyone else still sees them. `Unblock` undoes it, `BlockedPeers` and `BlockChanged` let the
app keep the list (`ChatNodeOptions.BlockedPeers` restores it on the next start). Your app still passes a room's
messages along as it always did; a block does not decide what other people get. In the Desktop app, right-click someone
in the People list or one of their messages. Blocked people stay in the People list, marked "blocked", so they can be
unblocked.

Blocking someone does not stop them seeing you in rooms you share, or your messages: they can only be kept out by removing their access to the room.

## Room owner and admins

A room made by this version names its owner (the account that made it) inside the invite, and the owner's devices sign statements appointing or removing admins, passed around the room like the soundboard list (`ChatNode.Roles.cs`). Only the owner appoints, so an admin cannot hand the role on and a removed admin cannot backdate a statement. The owner is fixed for the life of the room. A lost or stolen device of the owner can appoint admins for good: every linked device holds the account key itself, so it cannot be revoked, and the owner cannot be changed. This is accepted for now and the app says so when a room is made; the answer is a new room. The real fix is keeping the account key on one device (see [Devices and accounts](devices-and-accounts.md)). Rooms made by earlier versions have no owner and no admins; the way to get them is a new room.

## Bans and admin deletions

Bans and admin deletions of messages and sounds (`ChatNode.Moderation.cs`) are signed orders from the owner or an admin, passed around the room, checked by every client against the current admins, so an order lapses when its admin is removed; an admin cannot ban an admin or the owner, and the owner's word on a person beats an admin's. A banned person leaves the member list, voice and everything sent, and their messages are hidden; they keep the room key, so this is soft.

## Replacing the room secret

Replacing the room secret (`ChatNode.Rekey.cs`) makes a ban hold: the owner (only) gives the room a new secret and hands it to the accounts in the room now, the other members pass it on over their encrypted connections to listed people only, and everyone else, banned people and new identities made to sneak in included, never sees it and can no longer find the room or answer its proofs. The room stays the same room (its first topic is its permanent id, which history, admins, bans and channels are tied to, and a rotated invite carries it with a count of replacements). People who are away when the key is replaced are not locked out: the list is everyone this device has seen in the room (kept in a roster, up to 256, most recent first), members keep the old places in the DHT up for 30 days and, when someone on the list turns up still holding an old key, hand them the new one over the encrypted connection (the connection proves their account; anyone not listed gets nothing).

Known gaps: someone away for more than 30 days, or never seen by the owner's device, needs the new invite from the owner; a member that joined after the key was replaced cannot recognise holders of keys older than the one it joined with, so only longer-standing members can hand over; only the owner can replace the key, so a lost owner device can still do it; if the owner replaces it from two devices at once the lower package hash wins, which can briefly split the room; everyone seen in the room is on the list, so a new identity that slipped in before a ban is given the key unless it is banned first; the dialogs have not been run by hand. An order that arrives before the statement making its signer an admin is dropped and only comes back when the peer next joins (there is no periodic resend); a person banned under one device id whose certificate we have not seen yet can still get through that device until we have.

## Channels

Every room has a first text and a first voice channel, and the owner and admins add, rename and remove more. Everyone can use every channel; this is organisation, not a lock, since everybody still gets every message (private channels would need a key per channel, a redesign). A message carries its channel and is signed with it; the history limit is per channel; a voice session is in one voice channel at a time, each with its own tree; a screen share belongs to the voice channel its sharer is in.

Not done: the desktop buttons and menus have not been run by hand; a channel removed while you are in it only sends you out of voice in the desktop app (the library leaves the session running); unread counts are not kept across restarts; the channel on show is not remembered; channel order cannot be changed after creation.
