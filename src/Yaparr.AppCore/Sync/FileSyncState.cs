using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.AppCore;

/// <summary>
/// <see cref="ISyncState"/> kept in a file, sealed like the other files with secrets (it holds the room keys, as the settings do), so a device that is restarted carries on
/// from where it was and still sends what it had written but not yet sent. It is not part of a backup: another device is made from the account's own copy.
/// </summary>
public sealed class FileSyncState : ISyncState
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private sealed class FileEntry
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Clock { get; set; } = "";
        public string Device { get; set; } = "";
        public bool Deleted { get; set; }
        public string Value { get; set; } = "";
        public bool Dirty { get; set; }
    }

    private sealed class FileBody
    {
        public long Cursor { get; set; }
        public List<FileEntry> Entries { get; set; } = new();
    }

    private readonly string _path;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, SyncEntry> _entries = new();
    private readonly HashSet<string> _dirty = new();
    private readonly object _gate = new();
    private long _cursor;

    public FileSyncState(string path, Action<string>? log = null)
    {
        _path = path;
        _log = log;
        Load();
    }

    public long Cursor
    {
        get { lock (_gate) return _cursor; }
        set { lock (_gate) { _cursor = value; Save(); } }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var body = JsonSerializer.Deserialize<FileBody>(SecretStore.ReadAllBytes(_path), Json);
            if (body == null) return;
            _cursor = body.Cursor;
            foreach (var e in body.Entries)
            {
                var entry = new SyncEntry(e.Kind, e.Id, e.Clock, e.Device, e.Deleted, Convert.FromBase64String(e.Value));
                _entries[entry.Key] = entry;
                if (e.Dirty) _dirty.Add(entry.Key);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // Not believed: this device reads everything again and writes what it holds, which the merge settles.
            _log?.Invoke("sync: could not read the saved state, starting again: " + ex.Message);
            _entries.Clear();
            _dirty.Clear();
            _cursor = 0;
        }
    }

    // Called with the gate held.
    private void Save()
    {
        try
        {
            var body = new FileBody
            {
                Cursor = _cursor,
                Entries = _entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => new FileEntry { Kind = e.Kind, Id = e.Id, Clock = e.Clock, Device = e.Device, Deleted = e.Deleted, Value = Convert.ToBase64String(e.Value), Dirty = _dirty.Contains(e.Key) })
                    .ToList(),
            };
            SecretStore.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(body, Json));
            AppData.MakePrivate(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("sync: could not save the state: " + ex.Message);
        }
    }

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
            Save();
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
            if (_entries.TryGetValue(entry.Key, out var held) && held.Clock == entry.Clock && _dirty.Remove(entry.Key)) Save();
        }
    }

    public IReadOnlyList<SyncEntry> All()
    {
        lock (_gate) return _entries.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToList();
    }
}
