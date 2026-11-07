using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <param name="Chan">The text channel, or null for the first one. Absent from messages by earlier versions, which belong to the first channel.</param>
public sealed record StoredMessage(string From, string Pub, string Name, string Id, long Ts, string Text, string Sig, string? Chan = null);

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
    private readonly HistoryCipher? _cipher;

    /// <summary>Without a <paramref name="cipher"/> the files are plain text.</summary>
    public FileHistoryStore(string directory, HistoryCipher? cipher = null)
    {
        _directory = directory;
        _cipher = cipher;
        Directory.CreateDirectory(directory);
    }

    private string Seal(string line, string path) => _cipher == null ? line : _cipher.Seal(line, Path.GetFileName(path));

    private string PathFor(string roomKey) => Path.Combine(_directory, roomKey + ".jsonl");

    public IReadOnlyList<StoredMessage> Load(string roomKey)
    {
        lock (_gate)
        {
            var path = PathFor(roomKey);
            if (!File.Exists(path)) return Array.Empty<StoredMessage>();
            var result = new List<StoredMessage>();
            bool plain = false;
            foreach (var stored in File.ReadLines(path))
            {
                bool wasPlain = false;
                var line = _cipher == null ? stored : _cipher.Open(stored, Path.GetFileName(path), out wasPlain);
                plain |= wasPlain;
                if (line == null) continue;
                try { if (JsonSerializer.Deserialize<StoredMessage>(line, Json) is { } m) result.Add(m); }
                catch (JsonException) {  }
            }
            // Written before history was sealed: put it back sealed.
            if (plain) WriteAll(path, result);
            return result;
        }
    }

    public void Append(string roomKey, StoredMessage message)
    {
        lock (_gate) File.AppendAllText(PathFor(roomKey), Seal(JsonSerializer.Serialize(message, Json), PathFor(roomKey)) + "\n");
    }

    public void Compact(string roomKey, IReadOnlyCollection<StoredMessage> keep)
    {
        lock (_gate)
        {
            WriteAll(PathFor(roomKey), keep);
        }
    }

    private void WriteAll(string path, IEnumerable<StoredMessage> messages)
    {
        var temp = path + ".tmp";
        File.WriteAllLines(temp, messages.Select(m => Seal(JsonSerializer.Serialize(m, Json), path)));
        File.Move(temp, path, overwrite: true);
    }
}

internal static class SignedMessage
{
    private const int MaxText = 8000, MaxName = 64, MaxId = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static StoredMessage Create(PeerIdentity identity, string roomKey, string name, string text, long? timestamp = null, string? channel = null)
    {
        var unsigned = new StoredMessage(
            identity.Id.Value, Convert.ToBase64String(identity.PublicKey), Truncate(name, MaxName),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), text, "", string.IsNullOrEmpty(channel) || channel == RoomChannels.DefaultText ? null : channel);
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredMessage m, string roomKey)
    {
        if (m.Text.Length > MaxText || m.Name.Length > MaxName || m.Id.Length is 0 or > MaxId) return false;
        if (m.Chan is { } chan && (chan.Length != 16 || !chan.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) || chan == RoomChannels.DefaultText)) return false;
        if (m.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(m.Pub);
            if (PeerId.FromPublicKey(pub).Value != m.From) return false;
            return PeerIdentity.Verify(pub, Payload(roomKey, m), Convert.FromBase64String(m.Sig));
        }
        catch (FormatException) { return false; }
    }

    public static string Key(StoredMessage m) => KeyOf(m.From, m.Id);

    public static string KeyOf(string from, string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(from + ":" + id)))[..32].ToLowerInvariant();

    private static byte[] Payload(string roomKey, StoredMessage m)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            // A message in the first channel is signed exactly as it always was, so earlier versions still accept it.
            w.Write(m.Chan == null ? "yaparr-msg-v1" : "yaparr-msg-v2"); w.Write(roomKey); w.Write(m.From); w.Write(m.Name);
            w.Write(m.Id); w.Write(m.Ts); w.Write(m.Text);
            if (m.Chan != null) w.Write(m.Chan);
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

    /// <summary>Forgets messages (by key), here and in the store, so they are not served to anyone else either.</summary>
    public void Remove(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            bool any = false;
            foreach (var k in keys) any |= _messages.Remove(k);
            if (any) _store?.Compact(_roomKey, _messages.Values.ToList());
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

    /// <summary>The messages held for these keys, oldest first (keys that are not held are left out).</summary>
    public List<StoredMessage> Find(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            Trim();
            return keys.Distinct().Select(k => _messages.GetValueOrDefault(k)).Where(m => m != null).Select(m => m!).OrderBy(m => m.Ts).ToList();
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

    /// <summary>How many reactions are kept for each message the history can hold: they are small, and a busy room reacts a lot.</summary>
    public const int ReactionBudget = 4;

    private void Trim()
    {
        long cutoff = OldestAllowed();
        foreach (var k in _messages.Where(kv => kv.Value.Ts < cutoff).Select(kv => kv.Key).ToList()) _messages.Remove(k);
        if (_messages.Count <= _max) return;
        // Reactions have a share of their own, so many of them do not push messages out of the history (and the other way round).
        var reactions = _messages.Where(kv => MessageEnvelope.IsReactionText(kv.Value.Text)).ToList();
        int allowed = _max * ReactionBudget;
        if (reactions.Count > allowed)
            foreach (var kv in reactions.OrderBy(kv => kv.Value.Ts).Take(reactions.Count - allowed)) _messages.Remove(kv.Key);
        // The limit is per channel, so a busy channel does not push a quiet one out of the history.
        foreach (var channel in _messages.Where(kv => !MessageEnvelope.IsReactionText(kv.Value.Text)).GroupBy(kv => kv.Value.Chan ?? "").Where(g => g.Count() > _max).ToList())
            foreach (var k in channel.OrderBy(kv => kv.Value.Ts).Take(channel.Count() - _max).Select(kv => kv.Key).ToList())
                _messages.Remove(k);
    }
}
