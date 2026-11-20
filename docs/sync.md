# Synced state (in progress)

The goal: one account on all of a person's devices, with the same rooms, name, blocks and shelves everywhere, and a room left on one device left on all. This page is about the part that exists so far, the **library** (`src/Yaparr.AppCore/Sync`) and the **server API** (Yaparr.Cloud, README, "Synced state"), and, in the computer app, the **name, rooms and blocks**, which now go through them. The settings that are the same on every device are entries too (below); message **history** and the **shelves** (own emoji and GIFs) are kept as pieces (below). The screens already say "synced" instead of "backup".

## How it works

Each thing that should be the same everywhere is an **entry**: a kind and an id (`room` / the room's id, `profile` / `name`, `block` / the person's id) and a value. A device that changes one makes a **write** of it, stamped with a clock; a device that deletes one (leaving a room) makes a write that marks it deleted. The server keeps the newest write of each entry and a cursor for each change, and nothing more: it never reads a value and never merges.

- **Locked before it leaves the device.** `SyncSealer` locks each value with AES-GCM under a key made (HKDF, under a name of its own) from the account's Sync key, the code that already opens backups and never leaves the devices. The entry's kind, id and clock are bound to the locked value, so the server cannot hand one entry's value to another, or an old value a newer clock, without it being noticed. The server sees kinds, ids, clocks, device ids and sizes.
- **One order of writes.** `SyncClock` is a hybrid logical clock: the time, a counter and the device id, as text that sorts in write order. A device that hears of a write from the future makes its next one newer still, so a device with a slow clock does not lose to what it has already seen. A write from more than a day ahead does not move the clock.
- **Merging is for each entry alone.** `SyncEngine.SyncAsync` reads what the other devices wrote after its cursor, keeps each entry whose write is newer than its own, announces those with `Remote` for the app to apply, then sends what it has written since. Every device picks the same winner for an entry, so devices end up in the same state in whatever order they sync. If the server says a write is stale (it holds a newer one) the next read settles it.
- **Leaving a room.** The deleted marker is a write like any other: a device that was away and still has the room does not bring it back, and joining the room again later is a newer write that replaces the marker.
- **Not believed.** A value that does not open with the account's key (made by someone else's, or changed on the way) is skipped and counted (`SyncReport.Unreadable`), not applied.

## The name, rooms and blocks

These were already the same on a person's devices while they were online together: each is an *account entry* (`room:<key>`, `block:<id>`, `name`) with a time, and the newest wins (`ChatNode`, `ChatNode.AccountSync.cs`). `AccountCloudSync` carries the same entries through the server, so a device that was off, or never online at the same time as the others, still gets them.

- The cloud copy of an entry is the account entry itself (value, time, deleted), locked. What comes back is handed to `ChatNode.ApplyCloudEntries`, which keeps it only if it is newer *by the rule the devices already use*; the cloud never overrules that rule. The server's own order (the clock) decides only which write it keeps; if that puts an older entry ahead of a newer one (two devices writing in the same millisecond), the device with the newer entry writes it again, and the next sync settles it.
- What is read in a chat stays between the devices and is not sent to the server.
- Runs about 20 seconds after the app opens, every five minutes, and a few seconds after a change (`ChatNode.AccountChanged`). It needs the device to be signed in to a server and to have a Sync key (made at the first backup by hand); until then it does nothing.
- The state (cursor, entries, what is waiting to be sent) is a sealed file `sync-<server and account>.json` (`FileSyncState`), not part of a backup.

## Settings

The preferences a person wants the same everywhere are `setting:<name>` account entries, one for each thing, so changing one never touches the others and the newer write wins by the usual rule. Which ones is decided in one place, `SettingsSync` (AppCore): the **muted rooms** (`muted:<room id>`), the **rooms that notify for every message** (`notify-all:<room id>`), **a room's sound** (`room-sound:<room id>`), the **message sound** (`message-sound`), **mentions only** (`only-mentions`), **notifications on or off** (`notify-messages`), **link previews** (`link-previews`) and **leaving messages for people who are away** (`leave-for-absent`). The look of the app, the audio devices and voice settings, push-to-talk, the codecs, what the window does when closed, updates and the VPN belong to a device and are never synced.

- A list is one entry for each item, and a value that is the default has no entry: putting a setting back to its default is the entry marked deleted. A name this version does not know is kept in the account and ignored, so a newer version can add settings without older ones breaking.
- `ChatNode.SetAccountSetting`, `AccountSettings` and `SettingChanged` carry them (`ChatNode.Settings.cs`); they go to the cloud copy with the other entries (`IsCloudEntry`), so a device that was off, or never online with the others, gets them too.
- `SettingsPush` (AppCore) is what the apps use: **`Seed`** when the app starts takes what the account holds into the settings (the account's value wins where it has one) and gives the account what this device has and it lacks, once; **`Remote`** applies a change from another device; **`Local`** runs after the settings are saved here (`AppData.SettingsSaved`) and sends only what changed since it last looked, so a change that came from another device is not sent back.

Tests: `tests/Yaparr.Tests/SettingsSyncTests.cs`.

## History

Message history is far too big for a synced record and has to last, so it is kept as **pieces** on the server's kept store (Yaparr.Cloud README, "Kept pieces": never expires, a quota of its own, 1 GiB an account by default, read only with a sign-in). One piece is **one month (UTC) of one room or of one direct conversation**: `h-<room id>-<yyyyMM>` or `d-<person id>-<yyyyMM>`, every row of that month (messages and reactions) as the device's history store has them, compressed (`HistoryPieces`, `ChatNode.KeptHistory`). A month rather than a day because the account holds at most 2000 synced records and a record is what says which piece is current, so a year of ten rooms is a hundred and twenty.

- **Locked.** A piece is locked on the device with the Sync key (`SyncSealer`, bound to the piece's name) and put on the server under the SHA-256 of the locked bytes (`KeepClient`). The server sees opaque ids, sizes and times.
- **Which piece is current.** A synced record of kind `piece`, named like the piece, holds `{p: id, t: tag}`, the id of the piece and its tag (how many rows it had). It goes through the same engine as every other record, so the newest write wins on every device (`PieceSync`).
- **Writing.** A run compares, for each month, the number of rows the device has with the tag the account holds; where they differ it writes a new piece, sends the record, and only then removes the piece it replaced, so no device is ever pointed at one that is gone. A month with no change costs nothing. Past the quota nothing more is written and the log says so; a refusal for too many calls waits for the next run.
- **Reading.** A device that sees a new record for a piece downloads it, opens it (a piece locked with another key is skipped and counted) and takes the rows into its store: what it already has is left alone, so taking in is safe in any order and any number of times. Each row is **checked, not trusted**: a room's message has to carry a good signature for that room, and every row has to belong to the piece's room or conversation and month.
- **Two devices that wrote the same month apart** each take the other's rows in, so both hold the union; the tags then agree and whoever still differs writes it once more.
- Runs with the rest of the account sync (the same timers). It is also what a new device uses to get its history back without the backup.

**Removed messages stay removed.** A message that a moderator takes down (a ban's purge, a removal) is removed **for good** (`RoomHistory.RemoveForGood`, `IChatHistoryStore.ApplyRemoved`; in SQLite a `removed` table), unlike a plain `Remove`, which only forgets it on this device, and the store never keeps a removed message again, whether another member's device offers it or an old month brings it back. The keys travel as one more piece for each room, `x-<room id>` (`HistoryPieces`): taking it in deletes those messages on the device, and the month pieces written before the removal can be taken in before or after it, in any order, with the same result. A device that still held the message then has a different row count than the account, so it writes the month again without it, and the old copies are replaced. The list is kept for good (a key is a few dozen bytes), because a device that has been off for a year has to honour it too.

**A conversation that is open shows what was taken in.** Taking in a piece that changed anything tells the node (`ChatNode.HistoryChangedBySync`), which raises `ChatRoom.HistoryChanged` or `DirectChat.HistoryChanged`; both apps read the window again for that conversation (a room's, including what was removed; a direct chat's, adding what is missing).

**A very busy month** (more than 100,000 rows in a room or chat) is kept as a piece for each day instead of one for the month (`h-<room>-yyyyMMdd`), so no piece comes near the 15 MiB the server takes; a device reads either kind. **A big first sync** (a new device taking in many pieces, or a first sync keeping many) says how far it has come: "Syncing your account… 12 of 40" in the banner above the room list on the phone and in the status line on the computer (`PieceSync.Progress`), only when there is a good deal to do.

Tests: `tests/Yaparr.Tests/HistoryPieceTests.cs`.

## The shelves

A person's own **custom emoji** (`e-<name>`) and the pictures on their **GIF shelf** (`g-<hash of the picture>`) are pieces too, one for each, holding the picture itself (`ShelfPieceSource`). An emoji's tag is a hash of its picture, so putting another picture under the same name is a change that goes out; a GIF's name is already a hash of its picture, so it never changes. A piece that is not what its name says (a GIF whose hash is not its name, an emoji that is not a picture) is not taken. The order of the GIF shelf (what was sent last) and the rooms' own emoji and sounds are not part of this: the first stays on the device, the others belong to the room.

Unlike history, **removing** one is a removal for the account: the device writes the piece's record as deleted and removes the piece from the server, and another device that reads the removal takes it off its shelf. To tell a piece taken off a shelf from one this device never had, each device keeps the names its shelves had at its last run (a sealed file `sync-<server and account>.known`), so a device that has only just joined, or never took a piece in, never removes anything from the account.

Tests: `ShelfPieceTests` in `tests/Yaparr.Tests/HistoryPieceTests.cs`.

## Bringing an account back

A new device (or a reinstalled one) can be set up from the synced state alone, without a whole-profile backup. The account's **identity** is a piece too (`k-identity`, `IdentityPieceSource`): the identity file of any linked device (every one holds the account key), locked with the Sync key like every other piece. It is written once, by whichever device gets there first (its tag is fixed, so devices do not rewrite it for each other), and a running device never takes it in, so a sync can never replace the identity a device has.

**Restore from Yaparr Cloud** (first-start screen, both apps) signs in, then `SyncRestore` reads the account's records, finds the identity piece and the account's name, and opens them with the Sync key. The new device gets a device key of its own, vouched for by the account key, exactly as when a device is linked or a backup is restored, so the old devices can stay in use. Nothing else is fetched there: once the app runs, the usual sync brings the rooms, blocks, pins, settings, history and shelves. A wrong Sync key is said so; an account that holds only whole-profile backups (made before this) falls back to the newest of them, as before.

Tests: `SyncRestoreTests` in `tests/Yaparr.Tests/HistoryPieceTests.cs`.

The whole-profile backup is still written alongside, for now, so an account can be restored by an older version and by the fallback; it is removed in a later release, once the apps in use all write the identity piece.

## Replacing the Sync key

For a key that was lost, or that someone else has seen: **Replace Sync key…** on the sync page of either app (`SyncKeyReplacement`). It goes in an order that leaves nothing half done:

1. The device syncs with the old key, so it holds all of what the account has.
2. It keeps what the old state held (which records, which pieces), saves the new key and drops the sync state (`sync-<server and account>.json` and its `.known` file), so the next run writes **everything** again under the new key. The automatic backup is made due, so a whole-profile backup is made with the new key too.
3. The sync runs with the new key. If it stops half way, the next ordinary sync finishes it: nothing is held as written. The old records come back as unreadable and are skipped.
4. What the old key locked is removed from the server: the old records the new state does not hold (written as deleted, which needs no key), the pieces they pointed at, and the whole-profile backups.

**Other devices** keep the old key until they are given the new one. A device that pulls records it cannot open (`SyncEngine.HaltOnUnreadable`, on for the ordinary sync) **stops**: it sends nothing, so it does not write over the new records with the old key, and it leaves its cursor where it was so the records are read again later. The sync page says *Your Sync key was replaced on another device. Enter the new key to keep syncing*, and the automatic backup waits too (a backup locked with the old key would be one nobody could open). **Replace Sync key… > I have the new key** takes the key (`SyncKeyReplacement.AdoptAsync` checks that it opens a record the server holds, so a slip cannot lock a device out), and the sync carries on from where it was: the state holds only what the device has already read.

Limits: a device that is off when the key is replaced and is later given the **old** key only (a restore from an old note) sees records it cannot open and halts, with the message above, until it has the new one; and another device making a *second* replacement at the same moment is a race the later of them wins, the other halting.

Tests: `tests/Yaparr.Tests/SyncKeyReplacementTests.cs`.

## Pins

Pinned rooms and chats (see [The desktop app](desktop-app.md#pinning-rooms-and-chats)) are account entries too, one for each pinned room or chat: `pin:r:<room id>` and `pin:d:<person id>`. The value is the pin's **position** among the pinned ones, a short text of lowercase digits and letters that sorts into the order they are shown in (`PinOrder`); an unpin is the entry marked deleted. `PinOrder.Between(before, after)` always makes a position strictly between two others, so moving one pinned room changes only its own entry: two devices that each move a different one never overwrite each other, and the newer write of the same pin wins by the rule above. Pinning one more goes after the last, and moving to the top goes before the first, in steps of a digit, so a position grows by a character only about every 17th time that is done over and over.

`ChatNode.Pins` lists them first to last, `Pin`, `Unpin` and `MovePin` change them (and raise `PinsChanged`, also when another device or the cloud copy changed one), and leaving a room unpins it. A pin for a room or chat this device does not have is kept and not shown. They go through `AccountCloudSync` with the rest (they are `account` entries to the server), so no server change was needed. `PinnedFirst` (AppCore) is what both apps use to keep the pinned ones at the top of a list and to work out, for a move, the positions of the neighbours.

Tests: `tests/Yaparr.Tests/PinTests.cs`.

## Parts

| | |
|---|---|
| `SyncClock` | makes and reads clocks |
| `SyncSealer` | locks and opens values |
| `SyncClient`, `ISyncTransport` | the two calls to the server, `GET` and `PUT /v1/sync` |
| `SyncEngine`, `ISyncState`, `MemorySyncState`, `FileSyncState` | the writes, the merge and the sync; the state is what a device keeps between syncs (a sealed file in the app, in memory in tests) |
| `AccountCloudSync` | the name, rooms, blocks and pins of `ChatNode` through the engine |
| `KeepClient`, `IKeepTransport` | the three calls of the kept store, `PUT`, `GET` and `DELETE /v1/keep/{id}` |
| `PieceSync`, `IPieceSource`, `HistoryPieceSource` | locking, keeping and taking in pieces; history is a source of them (`HistoryPieces` in the library does the months) |
| `PinOrder`, `PinnedFirst` | the positions of pinned rooms and chats, and keeping them at the top of a list |

Tests: `tests/Yaparr.Tests/AccountCloudSyncTests.cs` (two real nodes of one account that are never online together), `tests/Yaparr.Tests/SyncTests.cs` (the engine runs against a fake server that keeps records the way the real one does), and Yaparr.Cloud's `SyncStoreTests` and `SyncApiTests`.

## Known limits

- A device with a clock far wrong in the **future** makes writes that stay newest until real time catches up. The clock does not follow such a device, but the server does not refuse its writes. A check on the server (a clock too far ahead of its own time) is possible later.
- On the phone it runs while the app is open (the same timers); it does not wake the phone by itself.
- A room left on one device is a deleted *entry* that stays on the server (a few hundred bytes each, counted against the account's limit).
