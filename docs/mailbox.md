# Mailbox: messages and pictures for people who are away

## What Yaparr Cloud is, and is not

Yaparr is a peer-to-peer app. Yaparr Cloud is an optional extra that adds things the peers cannot do alone, and it follows these rules:

- **Peer to peer first.** Messages, pictures, voice and screens go straight between devices. The cloud never carries live traffic.
- **The cloud is a last resort.** A locked copy of a message or picture is left on the server only when someone you know is away and cannot have got it peer to peer, and only after a minute in which it could still have reached them. When everyone known is connected, nothing is left.
- **Short and sealed.** What is left stays for 7 days (about as long as the history that devices hand each other), is locked with a key the server never has, and goes when the account is deleted.
- **You choose.** "Leave messages for people who are away" (Settings > Yaparr Cloud) is on by default for people signed in to a server; off, nothing of yours is left there. The app works fully with no account at all.
- **Anyone can run it.** The server is open source and self-hostable; the apps work with any Yaparr Cloud server.

**Status.** Built, in the server (Yaparr.Cloud, "Mailboxes" and "Blobs" in its README) and in the apps (`MailboxSync` and `BlobRelayClient` in `Yaparr.AppCore`; `ChatNode.Mailbox.cs`, `BlobRelay.cs` and `ChatNode.DirectMailbox.cs` in `Yaparr`): room messages, pictures in rooms and direct chats, and direct messages. Not tried on real devices yet.

[Documentation](README.md)

Today a message reaches someone only when one of their devices is online together with one that has the message (see [Rooms and messages](rooms-and-messages.md), "Catch-up"). The **mailbox** closes that gap with Yaparr Cloud: while a friend is away, a friend's app leaves the message, locked, on the server, and the friend's devices fetch it when they come back. The same goes for pictures.

**The promise, and why it can be kept.** Nobody at Yaparr, and nobody who runs the server, can read what is in the mailbox. That is not a policy: the server is never given the means. Everything it stores is locked on a device with a key made from something only the members of the room have (the room's secret), and the server does not have that secret. It keeps the same promise as the backup and the synced settings ([Backup server](backup-server.md), [Synced state](sync.md)).

What the server **can** see is told in "What the server sees" below, and the privacy page repeats it.

## Why a mailbox and not a hosted device

An always-on device that lives on the server ([#51](https://github.com/zburtle/Yaparr/issues/51)) would have to hold the person's identity keys and read their messages to be a device, so the server could read them. A mailbox only holds locked messages and hands them on, so it works for everyone who signs up to Yaparr Cloud, costs storage rather than a running process for each person, and many thousands of people fit on one server. A true always-on device stays an option to run on hardware you own (the issue as written).

## Rooms (the first step)

### The mailbox of a room

Two values come from the room's current secret with HKDF (the way `RoomKey` already derives its topic and its proof key):

- **mailbox id** (`yaparr/mailbox-id/v1`): 32 bytes, written base64url. It names the mailbox. Only members know it.
- **mailbox key** (`yaparr/mailbox-key/v1`): 32 bytes, an AES-256-GCM key. It never leaves the devices.

When the owner replaces the room's secret to lock someone out, both change: the new mailbox starts empty and the person who was removed can neither read nor write it.

### What is stored

A message that is already signed by its author (`SignedMessage`, the same bytes that members hand each other in history) is sealed: AES-GCM under the mailbox key, a fresh random 12-byte nonce, the mailbox id as associated data. The server stores the nonce, ciphertext and tag as one base64 value, with the message's **key** (`ChatMessage.Key`: 32 hex characters that already name the message everywhere) as its item id, so posting the same message twice stores it once.

The reader opens it, checks the author's signature and hands it to the room as a message caught up from a member: the same checks, blocks, bans, deletions and history. The server is never trusted for anything. A forged or changed item does not open or does not verify, and is dropped. A message written in the last ten minutes is announced like a live one (a notification), so a phone that a push woke tells its owner; an older one is shown as a missed message, without a sound.

### When a message is posted

Not for every message. A message a person writes in a room is sealed and queued, and a minute later (`ChatNode.MailboxWait`) it is left in the mailbox **if someone is still known to be away**: anyone this device has seen in the room in the last 7 days who is not connected now, and who is not blocked or banned there. If everyone known is connected, the message is dropped from the queue and never reaches the server. A direct message is left only if the other person has no device connected when the minute is over. The person can turn leaving off altogether (`ChatNode.MailboxLeaving`, `AppSettings.LeaveForAbsent`).

- **Who counts as known.** The list a room keeps of who has been in it (the same list the owner uses to hand out a new secret). It exists for rooms that have an owner, which is every room made by the apps; a room made by a very old version has no such list, so nothing is left for it and its messages travel peer to peer only.
- **Not exact on purpose.** Someone who received the message live and then went away within the minute still counts as away, so a copy may be left that was not needed. A member the device has never seen in the room (they joined while it was offline) is not known, and gets what the others hand over when they next meet, as before.
- **Background.** The post is made with the device's own Yaparr Cloud sign-in and does not change what the sender sees.
- **In memory.** The queue is held in memory (at most 500). If the app is closed before a message is left, it is not left; it still reaches people the usual way when they are online together.

### When a mailbox is read

Soon after the app opens, every five minutes while it runs, a few seconds after something is due to be left (see above), and the moment a push wakes the phone, without waiting for a connection. The device remembers the last cursor it read for each mailbox in a sealed file; without it (a new install) it reads what is kept again and the room skips what it holds.

### Server

`GET` and `POST` `/v1/mailbox/{id}` for signed-in devices. Items are kept in the order they arrived, with a cursor for each (like the synced records). Limits, all configurable: an item is at most 32 KB; a mailbox holds at most 2000 items; an account may post a limited number per minute and per day; items are removed after **7 days** (`MailboxKeepDays`), whether read or not. Items older than that are removed on every post and by a sweep at start. The account that posted an item is kept with it, to count the quota and so that deleting an account can delete what it posted (see below); it is never sent to a reader.

## Pictures and GIFs

A picture today goes straight from the sender to whoever asks, and is addressed by its hash. For people who are offline it also waits on the server, locked:

1. Before it is sent, **if someone is away** (`ChatNode.RelayAttachmentAsync`, called by both apps after `PrepareAttachment`, with the room or the other person; the same rule as for messages, and not at all if the person turned leaving off), the sender's app encrypts the file (AES-256-GCM under a fresh random key, the picture's hash as associated data) and uploads the locked bytes with `PUT /v1/blob/{id}`, where the id is the SHA-256 of the locked bytes; the server checks that, so nobody picks an id.
2. The message's picture description gains the blob's id, the key and the server's address (`B`, `K`, `V` in `Attachment`). They travel **inside the message**, which is sealed in the mailbox and signed like any other, so the server sees neither the key nor which message a blob belongs to. The 128-pixel thumbnail is made by the sender, as before: the server cannot make one.
3. A reader that has no connected member holding the picture fetches the locked bytes with `GET /v1/blob/{id}`, checks they hash to the id, opens them, checks that the result hashes to the picture's own hash, and stores it like any picture. Size limits and the wait-for-a-click rules for big files and for strangers are the ones that already apply.

- **Only the reader's own server.** A reader fetches only from the server it is signed in to, and only when the message names that same server. A message cannot make the app contact a server the sender chose (that would show the reader's address to it), so a picture left on someone else's server is fetched between members as before.
- **No sign-in to fetch.** The id is as unguessable as the file is secret, a fetch carries no account, and so the server cannot tie a read to a person.
- **Limits.** A file is at most 9 MB locked (`BlobMaxBytes`; a picture is at most 8 MB); an account may hold 256 MB of pictures that have not expired (`BlobQuotaBytes`); a blob is removed after 7 days (`BlobKeepDays`) and when its account is deleted; uploads are rate limited. There is no deduplication: every upload has its own key.
- **Not covered yet.** Custom emoji, avatars, sounds and the room shelves keep working between members as before.
- **Where the files are.** Locked pictures are files in the server's data folder (`blobs`), also when the server keeps its records in PostgreSQL; a bucket (S3) is not used for them yet.

## Direct messages

A conversation has no room secret to lock a mailbox with, and a person's linked devices do not share a private key (each has its own, with a certificate from the account), so the two people share a secret of their own:

- **Making it.** The first of the two to need one (when a direct message is written and the device is signed in to a server) makes 32 random bytes and keeps them as one more account setting, `dm:<the other account>`, which travels like the name, rooms and blocks: between the person's devices by their own sync, and through the account's cloud copy, locked with the recovery code. That is how all of a person's devices have it.
- **Giving it to the other side.** Over the connection to a device of theirs (`Wire.DmKey`): the connection is already authenticated and encrypted, so only they receive it. It is sent before the first message, and again whenever a device of theirs connects. The receiver keeps it as `dm:<the sender's account>`. If both make one at once, the secret of the account with the lower id is kept by both. A conversation therefore needs one live connection first, as a first message always did; after that it works while either is away.
- **The mailbox.** Id and key come from the secret with HKDF, bound to the two account ids in a fixed order, so both sides and every device compute the same ones, and nobody else can. The server sees an opaque id and cannot tell it from a room's.
- **What is stored.** The signed message record (`DirectProof`: the writing device's key, the account's certificate for it, the signature over the recipient, the device, the id, the time and the words), sealed. The reader checks it as it checks a message that arrives over a connection: that it was written by the other person's account or by its own, for the right person, with a valid signature. A blocked person's messages are dropped. A message written on one of your own other devices is kept in the history and shown when the chat is next opened.
- **Pictures** work as in rooms.

## What the server sees

- That an account posted to, or fetched from, a mailbox (the mailbox id is an opaque 43-character value), and when, from which address, and how big the item was. The server cannot tell a room's mailbox from a conversation's, or what the members are called; two accounts that touch the same mailbox id are in the same room or conversation.
- How big and how many the blobs are, which account uploaded each, and when. A fetch of a blob is anonymous: the address it came from and the time, no account.
- Nothing of any message, name, picture, link or caption.

What the server cannot do: read an item, change one without the reader noticing, make one that a room accepts, or tell a mailbox from a person.

## Deleting

- **An account's own data:** deleting the account removes its blobs, and the items it posted to mailboxes (rooms and conversations). What other people had already fetched stays with them.
- **Everything else** expires after 7 days.
- **Turning it off:** nothing is posted if the device is signed out. A later setting can let a person keep the mailbox from being used for their messages; that is not decided here.

## Abuse

The server cannot inspect what it holds, so it cannot be a moderator. Its tools are limits (item size, mailbox size, per-account rates and quotas), accounts that can be removed by whoever runs the server (which also removes what they posted), and the same room rules the members already have: a banned or blocked author's message is dropped by the reader. Whoever runs a public server should have the legal advice that applies where they are before offering a store of content they cannot see.

## Order of work

1. This page. Done.
2. Server: mailbox store (files and PostgreSQL, one set of tests for both), the API, limits, expiry, deletion (Yaparr.Cloud). Done. The PostgreSQL store is tested only where `YAPARR_TEST_POSTGRES` is set.
3. Rooms in the app: post, fetch on a timer and on a wake, seal and open. Done.
4. Pictures: the blob store and API, the sender's upload and the reader's fetch. Done.
5. Direct messages. Done.

## Not done

- The mailbox and blob calls are made over the signed-in device's own token and nothing is retried beyond the next run; a message queued in memory is lost if the app is closed before it is sent.
- Blobs in a bucket (S3), custom emoji, avatars and sounds through the server.
- A setting to keep a person's own messages out of mailboxes.
- Trying all of it on real devices, with a phone that a push wakes.
