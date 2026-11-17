# Synced state (in progress)

The goal: one account on all of a person's devices, with the same rooms, name, blocks and shelves everywhere, and a room left on one device left on all. This page is about the part that exists so far, the **library** (`src/Yaparr.AppCore/Sync`) and the **server API** (Yaparr.Cloud, README, "Synced state"), and, in the computer app, the **name, rooms and blocks**, which now go through them. The next steps move the shelves and settings, and then the screens say "synced" instead of "backup".

What is not here: message history. That is already shared between devices by the peers themselves, and a copy for when no other device is online stays a backup (`docs/backup-server.md`).

## How it works

Each thing that should be the same everywhere is an **entry**: a kind and an id (`room` / the room's id, `profile` / `name`, `block` / the person's id) and a value. A device that changes one makes a **write** of it, stamped with a clock; a device that deletes one (leaving a room) makes a write that marks it deleted. The server keeps the newest write of each entry and a cursor for each change, and nothing more: it never reads a value and never merges.

- **Locked before it leaves the device.** `SyncSealer` locks each value with AES-GCM under a key made (HKDF, under a name of its own) from the account's recovery code, the code that already opens backups and never leaves the devices. The entry's kind, id and clock are bound to the locked value, so the server cannot hand one entry's value to another, or an old value a newer clock, without it being noticed. The server sees kinds, ids, clocks, device ids and sizes.
- **One order of writes.** `SyncClock` is a hybrid logical clock: the time, a counter and the device id, as text that sorts in write order. A device that hears of a write from the future makes its next one newer still, so a device with a slow clock does not lose to what it has already seen. A write from more than a day ahead does not move the clock.
- **Merging is for each entry alone.** `SyncEngine.SyncAsync` reads what the other devices wrote after its cursor, keeps each entry whose write is newer than its own, announces those with `Remote` for the app to apply, then sends what it has written since. Every device picks the same winner for an entry, so devices end up in the same state in whatever order they sync. If the server says a write is stale (it holds a newer one) the next read settles it.
- **Leaving a room.** The deleted marker is a write like any other: a device that was away and still has the room does not bring it back, and joining the room again later is a newer write that replaces the marker.
- **Not believed.** A value that does not open with the account's key (made by someone else's, or changed on the way) is skipped and counted (`SyncReport.Unreadable`), not applied.

## The name, rooms and blocks

These were already the same on a person's devices while they were online together: each is an *account entry* (`room:<key>`, `block:<id>`, `name`) with a time, and the newest wins (`ChatNode`, `ChatNode.AccountSync.cs`). `AccountCloudSync` carries the same entries through the server, so a device that was off, or never online at the same time as the others, still gets them.

- The cloud copy of an entry is the account entry itself (value, time, deleted), locked. What comes back is handed to `ChatNode.ApplyCloudEntries`, which keeps it only if it is newer *by the rule the devices already use*; the cloud never overrules that rule. The server's own order (the clock) decides only which write it keeps; if that puts an older entry ahead of a newer one (two devices writing in the same millisecond), the device with the newer entry writes it again, and the next sync settles it.
- What is read in a chat stays between the devices and is not sent to the server.
- Runs about 20 seconds after the app opens, every five minutes, and a few seconds after a change (`ChatNode.AccountChanged`). It needs the device to be signed in to a server and to have a recovery code (made at the first backup by hand); until then it does nothing.
- The state (cursor, entries, what is waiting to be sent) is a sealed file `sync-<server and account>.json` (`FileSyncState`), not part of a backup.

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
| `PinOrder`, `PinnedFirst` | the positions of pinned rooms and chats, and keeping them at the top of a list |

Tests: `tests/Yaparr.Tests/AccountCloudSyncTests.cs` (two real nodes of one account that are never online together), `tests/Yaparr.Tests/SyncTests.cs` (the engine runs against a fake server that keeps records the way the real one does), and Yaparr.Cloud's `SyncStoreTests` and `SyncApiTests`.

## Known limits

- A device with a clock far wrong in the **future** makes writes that stay newest until real time catches up. The clock does not follow such a device, but the server does not refuse its writes. A check on the server (a clock too far ahead of its own time) is possible later.
- On the phone it runs while the app is open (the same timers); it does not wake the phone by itself.
- A room left on one device is a deleted *entry* that stays on the server (a few hundred bytes each, counted against the account's limit).
