using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed record StoredMessage(string From, string Pub, string Name, string Id, long Ts, string Text, string Sig);

public interface IChatHistoryStore
{
    IReadOnlyList<StoredMessage> Load(string roomKey);
    void Append(string roomKey, StoredMessage message);
    void Compact(string roomKey, IReadOnlyCollection<StoredMessage> keep);
}

public sealed class FileHistoryStore : IChatHistoryStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly object _gate = new();

    public FileHistoryStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string roomKey) => Path.Combine(_directory, roomKey + ".jsonl");

    public IReadOnlyList<StoredMessage> Load(string roomKey)
    {
        lock (_gate)
        {
            var path = PathFor(roomKey);
            if (!File.Exists(path)) return Array.Empty<StoredMessage>();
            var result = new List<StoredMessage>();
            foreach (var line in File.ReadLines(path))
            {
                try { if (JsonSerializer.Deserialize<StoredMessage>(line, Json) is { } m) result.Add(m); }
                catch (JsonException) {  }
            }
            return result;
        }
    }

    public void Append(string roomKey, StoredMessage message)
    {
        lock (_gate) File.AppendAllText(PathFor(roomKey), JsonSerializer.Serialize(message, Json) + "\n");
    }

    public void Compact(string roomKey, IReadOnlyCollection<StoredMessage> keep)
    {
        lock (_gate)
        {
            var temp = PathFor(roomKey) + ".tmp";
            File.WriteAllLines(temp, keep.Select(m => JsonSerializer.Serialize(m, Json)));
            File.Move(temp, PathFor(roomKey), overwrite: true);
        }
    }
}

internal static class SignedMessage
{
    private const int MaxText = 8000, MaxName = 64, MaxId = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static StoredMessage Create(PeerIdentity identity, string roomKey, string name, string text, long? timestamp = null)
    {
        var unsigned = new StoredMessage(
            identity.Id.Value, Convert.ToBase64String(identity.PublicKey), Truncate(name, MaxName),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), text, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredMessage m, string roomKey)
    {
        if (m.Text.Length > MaxText || m.Name.Length > MaxName || m.Id.Length is 0 or > MaxId) return false;
        if (m.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(m.Pub);
            if (PeerId.FromPublicKey(pub).Value != m.From) return false;
            return PeerIdentity.Verify(pub, Payload(roomKey, m), Convert.FromBase64String(m.Sig));
        }
        catch (FormatException) { return false; }
    }

    public static string Key(StoredMessage m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.From + ":" + m.Id)))[..32].ToLowerInvariant();

    private static byte[] Payload(string roomKey, StoredMessage m)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-msg-v1"); w.Write(roomKey); w.Write(m.From); w.Write(m.Name);
            w.Write(m.Id); w.Write(m.Ts); w.Write(m.Text);
        }
        return ms.ToArray();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

internal sealed class RoomHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredMessage> _messages = new();
    private readonly string _roomKey;
    private readonly IChatHistoryStore? _store;
    private readonly int _max;
    private readonly TimeSpan _maxAge;

    public RoomHistory(string roomKey, IChatHistoryStore? store, int max, TimeSpan maxAge)
    {
        _roomKey = roomKey;
        _store = store;
        _max = max;
        _maxAge = maxAge;

        if (store == null) return;
        var loaded = store.Load(roomKey);
        foreach (var m in loaded)
            if (SignedMessage.Verify(m, roomKey)) _messages[SignedMessage.Key(m)] = m;
        int before = _messages.Count;
        Trim();
        if (_messages.Count != before || loaded.Count != before) store.Compact(roomKey, _messages.Values.ToList());
    }

    public bool TryAdd(StoredMessage m)
    {
        lock (_gate)
        {
            if (m.Ts < OldestAllowed()) return false;
            if (!_messages.TryAdd(SignedMessage.Key(m), m)) return false;
            _store?.Append(_roomKey, m);
            Trim();
            return true;
        }
    }

    public IReadOnlyList<StoredMessage> Snapshot()
    {
        lock (_gate)
        {
            Trim();
            return _messages.Values.OrderBy(m => m.Ts).ThenBy(SignedMessage.Key, StringComparer.Ordinal).ToList();
        }
    }

    public string[] Keys()
    {
        lock (_gate)
        {
            Trim();
            return _messages.Keys.ToArray();
        }
    }

    public List<StoredMessage> Missing(HashSet<string> have, int cap)
    {
        lock (_gate)
        {
            Trim();
            return _messages.Where(kv => !have.Contains(kv.Key)).Select(kv => kv.Value)
                .OrderByDescending(m => m.Ts).Take(cap).OrderBy(m => m.Ts).ToList();
        }
    }

    private long OldestAllowed() => DateTimeOffset.UtcNow.Subtract(_maxAge).ToUnixTimeMilliseconds();

    private void Trim()
    {
        long cutoff = OldestAllowed();
        foreach (var k in _messages.Where(kv => kv.Value.Ts < cutoff).Select(kv => kv.Key).ToList()) _messages.Remove(k);
        if (_messages.Count <= _max) return;
        foreach (var k in _messages.OrderBy(kv => kv.Value.Ts).Take(_messages.Count - _max).Select(kv => kv.Key).ToList())
            _messages.Remove(k);
    }
}
