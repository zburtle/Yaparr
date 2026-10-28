using System.Net;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// Remembers where room members were last reached, so a restart can dial them straight away
/// instead of waiting for discovery to find them again.
/// </summary>
internal sealed class PeerCache
{
    private const int MaxPerRoom = 16;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string EndPoint { get; set; } = "";
        public long Seen { get; set; }
    }

    private readonly string? _path;
    private readonly object _gate = new();
    private Dictionary<string, List<Entry>> _rooms = new();
    private bool _dirty;

    public PeerCache(string? path)
    {
        _path = path;
        if (path == null || !File.Exists(path)) return;
        try
        {
            _rooms = JsonSerializer.Deserialize<Dictionary<string, List<Entry>>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
    }

    // Our own addresses (public address, port mapping, relay) end up in the DHT and come back in searches, sometimes
    // long after we stopped using them. They are kept under a reserved key so a restart can skip them too.
    private const string SelfKey = "(self)";
    private static readonly TimeSpan SelfAge = TimeSpan.FromHours(3);

    public void RememberSelf(IPEndPoint endPoint)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (!_rooms.TryGetValue(SelfKey, out var list)) _rooms[SelfKey] = list = new();
            string text = endPoint.ToString();
            list.RemoveAll(e => e.EndPoint == text || e.Seen < now - (long)SelfAge.TotalSeconds);
            list.Insert(0, new Entry { Id = "", EndPoint = text, Seen = now });
            if (list.Count > 256) list.RemoveRange(256, list.Count - 256);
            _dirty = true;
        }
    }

    public bool IsSelf(IPEndPoint endPoint)
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(SelfAge).ToUnixTimeSeconds();
        string text = endPoint.ToString();
        lock (_gate) return _rooms.TryGetValue(SelfKey, out var list) && list.Any(e => e.EndPoint == text && e.Seen >= cutoff);
    }

    public void Remember(string roomKey, PeerId id, IPEndPoint endPoint)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomKey, out var list)) _rooms[roomKey] = list = new();
            list.RemoveAll(e => e.Id == id.Value);
            list.Insert(0, new Entry { Id = id.Value, EndPoint = endPoint.ToString(), Seen = now });
            if (list.Count > MaxPerRoom) list.RemoveRange(MaxPerRoom, list.Count - MaxPerRoom);
            _dirty = true;
        }
    }

    public IReadOnlyList<(PeerId Id, IPEndPoint EndPoint)> For(string roomKey)
    {
        long cutoff = DateTimeOffset.UtcNow.Subtract(MaxAge).ToUnixTimeSeconds();
        var result = new List<(PeerId, IPEndPoint)>();
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomKey, out var list)) return result;
            foreach (var e in list.Where(e => e.Seen >= cutoff))
                if (e.Id.Length == 64 && IPEndPoint.TryParse(e.EndPoint, out var ep)) result.Add((new PeerId(e.Id), ep));
        }
        return result;
    }

    public void Save()
    {
        if (_path == null) return;
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(_rooms, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { lock (_gate) _dirty = true; }
    }
}
