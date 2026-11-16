using System.Security.Cryptography;

namespace Yaparr.AppCore;

/// <summary>One synced thing, as this device knows it: what it is (<see cref="Kind"/> and <see cref="Id"/>, like a room and its id), its value, and the clock of the write that made it so. A deleted one has no value and is kept as a marker.</summary>
public sealed record SyncEntry(string Kind, string Id, string Clock, string Device, bool Deleted, byte[] Value)
{
    public string Key => Kind + "/" + Id;
}

/// <summary>What a device keeps between syncs: the entries it knows (the newest write of each), which of them still have to be sent, and where in the server's order of changes it has read to.</summary>
public interface ISyncState
{
    /// <summary>Where the last pull ended (0 before any).</summary>
    long Cursor { get; set; }

    SyncEntry? Get(string kind, string id);

    /// <summary>Keeps an entry in place of the one held for it. <paramref name="dirty"/> says it still has to be sent (a write made here); false (what came from the server) clears that.</summary>
    void Put(SyncEntry entry, bool dirty);

    /// <summary>The entries that have to be sent, oldest write first.</summary>
    IReadOnlyList<SyncEntry> Dirty();

    /// <summary>The server took this write: it no longer has to be sent (unless a newer write of the same entry was made since).</summary>
    void MarkSent(SyncEntry entry);

    IReadOnlyList<SyncEntry> All();
}

/// <summary><see cref="ISyncState"/> in memory (for tests, and the base of a state kept in a file).</summary>
public sealed class MemorySyncState : ISyncState
{
    private readonly Dictionary<string, SyncEntry> _entries = new();
    private readonly HashSet<string> _dirty = new();
    private readonly object _gate = new();

    public long Cursor { get; set; }

    public SyncEntry? Get(string kind, string id)
    {
        lock (_gate) return _entries.GetValueOrDefault(kind + "/" + id);
    }

    public void Put(SyncEntry entry, bool dirty)
    {
        lock (_gate)
        {
            _entries[entry.Key] = entry;
            if (dirty) _dirty.Add(entry.Key); else _dirty.Remove(entry.Key);
        }
    }

    public IReadOnlyList<SyncEntry> Dirty()
    {
        lock (_gate) return _dirty.Select(k => _entries[k]).OrderBy(e => e.Clock, StringComparer.Ordinal).ToList();
    }

    public void MarkSent(SyncEntry entry)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Key, out var held) && held.Clock == entry.Clock) _dirty.Remove(entry.Key);
        }
    }

    public IReadOnlyList<SyncEntry> All()
    {
        lock (_gate) return _entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
    }
}

/// <summary>What one <see cref="SyncEngine.SyncAsync"/> did.</summary>
public sealed record SyncReport(int Received, int Sent, int Unreadable, bool Complete);

/// <summary>
/// Keeps the entries of one account the same on its devices, through a Yaparr Cloud server that only stores them. A device makes a write with <see cref="Set"/> or
/// <see cref="Remove"/> (stamped with a clock that is newer than anything it has seen, and kept to be sent), and <see cref="SyncAsync"/> reads what the other devices wrote,
/// merges it, and sends what is waiting. The merge is for each entry alone: the write with the newer clock wins, the same on every device, so they end up with the same
/// state whatever order they sync in. A remote write that wins is announced with <see cref="Remote"/> for the app to apply (a room to join, a name to show); the app
/// itself calls <see cref="Set"/> and <see cref="Remove"/> when the person changes one of these things.
/// </summary>
public sealed class SyncEngine
{
    private const int MaxRounds = 4;

    private readonly ISyncState _state;
    private readonly ISyncTransport _transport;
    private readonly SyncSealer _sealer;
    private readonly SyncClock _clock;
    private readonly string _device;
    private readonly SemaphoreSlim _running = new(1, 1);

    public SyncEngine(ISyncState state, ISyncTransport transport, SyncSealer sealer, SyncClock clock, string deviceId)
    {
        _state = state;
        _transport = transport;
        _sealer = sealer;
        _clock = clock;
        _device = deviceId;
    }

    /// <summary>A write from another device won over what this one had (or this device had nothing): the app applies it. A deleted entry has no value.</summary>
    public event Action<SyncEntry>? Remote;

    /// <summary>This device's write of an entry: kept, and sent at the next sync. Returns what was kept.</summary>
    public SyncEntry Set(string kind, string id, byte[] value)
    {
        var entry = new SyncEntry(kind, id, _clock.Next(), _device, false, (byte[])value.Clone());
        _state.Put(entry, dirty: true);
        return entry;
    }

    /// <summary>This device's deletion of an entry (leaving a room): a marker, sent at the next sync, that makes the other devices delete it too.</summary>
    public SyncEntry Remove(string kind, string id)
    {
        var entry = new SyncEntry(kind, id, _clock.Next(), _device, true, Array.Empty<byte>());
        _state.Put(entry, dirty: true);
        return entry;
    }

    public SyncEntry? Get(string kind, string id) => _state.Get(kind, id);

    /// <summary>
    /// Reads what changed, merges it, and sends what is waiting. A write the server turns down because it holds a newer one is settled by reading again, a few times over.
    /// Calls take turns. An error from the server (it cannot be reached, this device is signed out) is thrown, and what was read so far is kept.
    /// </summary>
    public async Task<SyncReport> SyncAsync(CancellationToken cancel = default)
    {
        await _running.WaitAsync(cancel);
        try
        {
            int received = 0, sent = 0, unreadable = 0;
            for (int round = 0; round < MaxRounds; round++)
            {
                var (r, u) = await PullAllAsync(cancel);
                received += r;
                unreadable += u;
                var (s, refused) = await PushAllAsync(cancel);
                sent += s;
                if (!refused) return new SyncReport(received, sent, unreadable, true);
            }
            return new SyncReport(received, sent, unreadable, Complete: _state.Dirty().Count == 0);
        }
        finally { _running.Release(); }
    }

    private async Task<(int Received, int Unreadable)> PullAllAsync(CancellationToken cancel)
    {
        int received = 0, unreadable = 0;
        while (true)
        {
            var page = await _transport.PullAsync(_state.Cursor, cancel);
            foreach (var wire in page.Records)
            {
                _clock.Receive(wire.Clock);
                SyncEntry entry;
                if (wire.Deleted) entry = new SyncEntry(wire.Kind, wire.Id, wire.Clock, wire.Device, true, Array.Empty<byte>());
                else
                {
                    try { entry = new SyncEntry(wire.Kind, wire.Id, wire.Clock, wire.Device, false, _sealer.Open(wire.Kind, wire.Id, wire.Clock, wire.Body)); }
                    catch (CryptographicException) { unreadable++; continue; }   // not made with this account's key, or changed on the way: not believed
                }
                var held = _state.Get(entry.Kind, entry.Id);
                if (held != null && !SyncClock.IsNewer(entry.Clock, held.Clock)) continue;   // this device's own write (or a newer one of its own): nothing to take
                _state.Put(entry, dirty: false);
                received++;
                Remote?.Invoke(entry);
            }
            if (page.Cursor > _state.Cursor) _state.Cursor = page.Cursor;
            if (!page.More || page.Records.Count == 0) return (received, unreadable);
        }
    }

    private async Task<(int Sent, bool Refused)> PushAllAsync(CancellationToken cancel)
    {
        int sent = 0;
        bool refused = false;
        var waiting = _state.Dirty();
        for (int i = 0; i < waiting.Count; i += SyncClient.MaxBatch)
        {
            var batch = waiting.Skip(i).Take(SyncClient.MaxBatch).ToList();
            var wire = batch.Select(e => new SyncWireRecord(e.Kind, e.Id, e.Clock, e.Device, e.Deleted, e.Deleted ? "" : _sealer.Seal(e.Kind, e.Id, e.Clock, e.Value))).ToList();
            var result = await _transport.PushAsync(wire, cancel);
            var stale = new HashSet<string>(result.Stale, StringComparer.Ordinal);
            foreach (var entry in batch)
            {
                if (stale.Contains(entry.Key)) { refused = true; continue; }
                _state.MarkSent(entry);
                sent++;
            }
        }
        return (sent, refused);
    }
}
