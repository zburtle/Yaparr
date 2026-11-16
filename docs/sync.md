# Synced state (in progress)

The goal: one account on all of a person's devices, with the same rooms, name, blocks and shelves everywhere, and a room left on one device left on all. This page is about the part that exists so far, the **library** (`src/Yaparr.AppCore/Sync`) and the **server API** (Yaparr.Cloud, README, "Synced state"). Nothing in the app uses them yet; the next steps move the name, rooms and blocks onto them, then the shelves and settings, and then the screens say "synced" instead of "backup".

What is not here: message history. That is already shared between devices by the peers themselves, and a copy for when no other device is online stays a backup (`docs/backup-server.md`).

## How it works

Each thing that should be the same everywhere is an **entry**: a kind and an id (`room` / the room's id, `profile` / `name`, `block` / the person's id) and a value. A device that changes one makes a **write** of it, stamped with a clock; a device that deletes one (leaving a room) makes a write that marks it deleted. The server keeps the newest write of each entry and a cursor for each change, and nothing more: it never reads a value and never merges.

- **Locked before it leaves the device.** `SyncSealer` locks each value with AES-GCM under a key made (HKDF, under a name of its own) from the account's recovery code, the code that already opens backups and never leaves the devices. The entry's kind, id and clock are bound to the locked value, so the server cannot hand one entry's value to another, or an old value a newer clock, without it being noticed. The server sees kinds, ids, clocks, device ids and sizes.
- **One order of writes.** `SyncClock` is a hybrid logical clock: the time, a counter and the device id, as text that sorts in write order. A device that hears of a write from the future makes its next one newer still, so a device with a slow clock does not lose to what it has already seen. A write from more than a day ahead does not move the clock.
- **Merging is for each entry alone.** `SyncEngine.SyncAsync` reads what the other devices wrote after its cursor, keeps each entry whose write is newer than its own, announces those with `Remote` for the app to apply, then sends what it has written since. Every device picks the same winner for an entry, so devices end up in the same state in whatever order they sync. If the server says a write is stale (it holds a newer one) the next read settles it.
- **Leaving a room.** The deleted marker is a write like any other: a device that was away and still has the room does not bring it back, and joining the room again later is a newer write that replaces the marker.
- **Not believed.** A value that does not open with the account's key (made by someone else's, or changed on the way) is skipped and counted (`SyncReport.Unreadable`), not applied.

## Parts

| | |
|---|---|
| `SyncClock` | makes and reads clocks |
| `SyncSealer` | locks and opens values |
| `SyncClient`, `ISyncTransport` | the two calls to the server, `GET` and `PUT /v1/sync` |
| `SyncEngine`, `ISyncState`, `MemorySyncState` | the writes, the merge and the sync; the state is what a device keeps between syncs (a file in the app, in memory in tests) |

Tests: `tests/Yaparr.Tests/SyncTests.cs` (the engine runs against a fake server that keeps records the way the real one does), and Yaparr.Cloud's `SyncStoreTests` and `SyncApiTests`.

## Known limits

- A device with a clock far wrong in the **future** makes writes that stay newest until real time catches up. The clock does not follow such a device, but the server does not refuse its writes. A check on the server (a clock too far ahead of its own time) is possible later.
- The state is not kept in a file yet, so a device would read everything again after a restart. The app's state will be a sealed file like the others.
