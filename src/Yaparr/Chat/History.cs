using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <param name="Chan">The text channel, or null for the first one. Absent from messages by earlier versions, which belong to the first channel.</param>
public sealed record StoredMessage(string From, string Pub, string Name, string Id, long Ts, string Text, string Sig, string? Chan = null);

/// <summary>
/// Where a room's messages are kept. Everything is kept for good; what a device shows at first, and what it hands to others, is a window
/// onto it (see <see cref="RoomHistory"/>), so a store answers questions about parts of the history and is never read whole.
/// Keys are the keys of the messages (<see cref="SignedMessage.Key"/>); a reaction's target is the key of the message it is on.
/// </summary>
public interface IChatHistoryStore
{
    /// <summary>Keeps a message. False when the room already has it. <paramref name="reactionTarget"/> is null for a message and the key of the message a reaction is on (empty when it is not a good one).</summary>
    bool Add(string roomKey, string key, StoredMessage message, string? reactionTarget);

    /// <summary>Whether any message kept here, in any room or conversation, has <paramref name="text"/> in it (a picture's hash, to know whether a file is still wanted).</summary>
    bool Mentions(string text);

    /// <summary>Keeps many messages at once (bringing history over from elsewhere); those already held are left alone. Returns how many were new.</summary>
    int AddMany(string roomKey, IEnumerable<(string Key, StoredMessage Message, string? Target)> messages)
    {
        int added = 0;
        foreach (var (key, message, target) in messages) if (Add(roomKey, key, message, target)) added++;
        return added;
    }

    /// <summary>Forgets the messages with these keys.</summary>
    void Remove(string roomKey, IReadOnlyCollection<string> keys);

    /// <summary>The newest <paramref name="perChannel"/> messages (not reactions) of each channel, in no particular order.</summary>
    IReadOnlyList<StoredMessage> Newest(string roomKey, int perChannel);

    /// <summary>Up to <paramref name="count"/> messages (not reactions) of one channel (null for the first) older than the one at <paramref name="ts"/> with key <paramref name="key"/>, newest first.</summary>
    IReadOnlyList<StoredMessage> Before(string roomKey, string? channel, long ts, string key, int count);

    /// <summary>The messages held for these keys (keys that are not held are left out).</summary>
    IReadOnlyList<StoredMessage> Find(string roomKey, IReadOnlyCollection<string> keys);

    /// <summary>Which of these keys the room holds a message for.</summary>
    HashSet<string> Held(string roomKey, IReadOnlyCollection<string> keys);

    /// <summary>The reactions on the messages with these keys.</summary>
    IReadOnlyList<StoredMessage> ReactionsTo(string roomKey, IReadOnlyCollection<string> targets);

    /// <summary>Everything sent by these devices.</summary>
    IReadOnlyList<StoredMessage> ByAuthors(string roomKey, IReadOnlyCollection<string> authors);

    /// <summary>
    /// The window that is shared with other members: the messages not older than <paramref name="sinceTs"/>, at most <paramref name="perChannel"/>
    /// of the newest for each channel and <paramref name="reactions"/> of the newest reactions.
    /// </summary>
    IReadOnlyList<(string Key, StoredMessage Message)> Window(string roomKey, long sinceTs, int perChannel, int reactions);

    /// <summary>The keys of <see cref="Window"/>.</summary>
    string[] WindowKeys(string roomKey, long sinceTs, int perChannel, int reactions);

    /// <summary>The rooms there are any messages of.</summary>
    IReadOnlyList<string> Rooms();

    /// <summary>For each month (<see cref="HistoryMonths"/>, UTC) a room has anything from: how many rows it has (messages and reactions). What is kept in sync works a month at a time.</summary>
    IReadOnlyDictionary<string, int> MonthCounts(string roomKey);

    /// <summary>Every row (message or reaction, with the key of what the reaction is on) of one month of a room.</summary>
    IReadOnlyList<(string Key, StoredMessage Message, string? Target)> InMonth(string roomKey, string month);
}

/// <summary>Months as history is divided for sync: UTC, written <c>yyyyMM</c>, so they sort in time order.</summary>
public static class HistoryMonths
{
    public static string Of(long unixMs) => DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(unixMs, 0, 253_402_300_799_999)).UtcDateTime.ToString("yyyyMM");

    public static bool IsMonth(string? month) => month is { Length: 6 } && month.All(char.IsAsciiDigit) && int.Parse(month[4..]) is >= 1 and <= 12 && int.Parse(month[..4]) is >= 1970 and <= 9999;

    /// <summary>The first millisecond of the month, and of the next one.</summary>
    public static (long From, long To) Range(string month)
    {
        var start = new DateTimeOffset(int.Parse(month[..4]), int.Parse(month[4..]), 1, 0, 0, 0, TimeSpan.Zero);
        return (start.ToUnixTimeMilliseconds(), start.AddMonths(1).ToUnixTimeMilliseconds());
    }
}

/// <summary>Keeps messages in memory only: for a node that has no store, and for tests.</summary>
public sealed class MemoryHistoryStore : IChatHistoryStore, IDirectHistoryStore
{
    private readonly Dictionary<string, Dictionary<string, (DirectRecord Record, string? Target)>> _chats = new();

    private Dictionary<string, (DirectRecord Record, string? Target)> Chat(string peer) =>
        _chats.TryGetValue(peer, out var chat) ? chat : _chats[peer] = new();

    public bool AddDirect(string key, DirectRecord record, string? reactionTarget)
    {
        lock (_gate) return Chat(record.Peer).TryAdd(key, (record, reactionTarget));
    }

    public IReadOnlyList<DirectRecord> DirectNewest(string peer, int count)
    {
        lock (_gate)
            return Chat(peer).Where(kv => kv.Value.Target == null).OrderByDescending(kv => kv.Value.Record.Ts).ThenByDescending(kv => kv.Key, StringComparer.Ordinal)
                .Take(count).Select(kv => kv.Value.Record).ToList();
    }

    public IReadOnlyList<DirectRecord> DirectBefore(string peer, long ts, string key, int count)
    {
        lock (_gate)
            return Chat(peer).Where(kv => kv.Value.Target == null && (kv.Value.Record.Ts < ts || kv.Value.Record.Ts == ts && string.CompareOrdinal(kv.Key, key) < 0))
                .OrderByDescending(kv => kv.Value.Record.Ts).ThenByDescending(kv => kv.Key, StringComparer.Ordinal).Take(count).Select(kv => kv.Value.Record).ToList();
    }

    public DirectRecord? DirectFind(string peer, string key)
    {
        lock (_gate) return Chat(peer).TryGetValue(key, out var v) ? v.Record : null;
    }

    public IReadOnlyList<DirectRecord> DirectReactionsTo(string peer, IReadOnlyCollection<string> targets)
    {
        var wanted = targets.ToHashSet();
        lock (_gate) return Chat(peer).Values.Where(v => v.Target != null && wanted.Contains(v.Target)).Select(v => v.Record).ToList();
    }

    public DirectRecord? DirectOwn(string peer, string id)
    {
        lock (_gate) return Chat(peer).Values.Select(v => v.Record).FirstOrDefault(r => r.Id == id && r.Mine);
    }

    public bool DirectAny(string peer)
    {
        lock (_gate) return _chats.TryGetValue(peer, out var chat) && chat.Count > 0;
    }

    public long DirectNewestIncoming(string peer)
    {
        lock (_gate) return Chat(peer).Values.Where(v => !v.Record.Mine).Select(v => v.Record.Ts).DefaultIfEmpty(0).Max();
    }

    public IReadOnlyList<string> DirectPeers()
    {
        lock (_gate) return _chats.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
    }

    public string DirectLastName(string peer)
    {
        lock (_gate)
            return Chat(peer).Values.Select(v => v.Record).Where(r => !r.Mine && r.Name.Length > 0).OrderByDescending(r => r.Ts).Select(r => r.Name).FirstOrDefault() ?? "";
    }

    public IReadOnlyList<(string Key, DirectRecord Record)> DirectWindow(long sinceTs, int perChat, int reactions, int cap)
    {
        lock (_gate)
        {
            var all = new List<(string Key, DirectRecord Record)>();
            foreach (var chat in _chats.Values)
            {
                var recent = chat.Where(kv => kv.Value.Record.Ts >= sinceTs).ToList();
                all.AddRange(recent.Where(kv => kv.Value.Target == null).OrderByDescending(kv => kv.Value.Record.Ts).Take(perChat).Select(kv => (kv.Key, kv.Value.Record)));
                all.AddRange(recent.Where(kv => kv.Value.Target != null).OrderByDescending(kv => kv.Value.Record.Ts).Take(reactions).Select(kv => (kv.Key, kv.Value.Record)));
            }
            return all.OrderByDescending(w => w.Record.Ts).Take(cap).ToList();
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, (StoredMessage Message, string? Target)>> _rooms = new();

    private Dictionary<string, (StoredMessage Message, string? Target)> Room(string roomKey) =>
        _rooms.TryGetValue(roomKey, out var room) ? room : _rooms[roomKey] = new();

    public bool Add(string roomKey, string key, StoredMessage message, string? reactionTarget)
    {
        lock (_gate) return Room(roomKey).TryAdd(key, (message, reactionTarget));
    }

    public void Remove(string roomKey, IReadOnlyCollection<string> keys)
    {
        lock (_gate) { var room = Room(roomKey); foreach (var k in keys) room.Remove(k); }
    }

    public bool Mentions(string text)
    {
        lock (_gate)
            return _rooms.Values.Any(r => r.Values.Any(v => v.Message.Text.Contains(text, StringComparison.Ordinal)))
                   || _chats.Values.Any(c => c.Values.Any(v => v.Record.Text.Contains(text, StringComparison.Ordinal)));
    }

    private static string Channel(StoredMessage m) => m.Chan ?? "";

    public IReadOnlyList<StoredMessage> Newest(string roomKey, int perChannel)
    {
        lock (_gate)
            return Room(roomKey).Where(kv => kv.Value.Target == null).GroupBy(kv => Channel(kv.Value.Message))
                .SelectMany(g => g.OrderByDescending(kv => kv.Value.Message.Ts).ThenByDescending(kv => kv.Key, StringComparer.Ordinal).Take(perChannel))
                .Select(kv => kv.Value.Message).ToList();
    }

    public IReadOnlyList<StoredMessage> Before(string roomKey, string? channel, long ts, string key, int count)
    {
        lock (_gate)
            return Room(roomKey).Where(kv => kv.Value.Target == null && Channel(kv.Value.Message) == (channel ?? "")
                                             && (kv.Value.Message.Ts < ts || kv.Value.Message.Ts == ts && string.CompareOrdinal(kv.Key, key) < 0))
                .OrderByDescending(kv => kv.Value.Message.Ts).ThenByDescending(kv => kv.Key, StringComparer.Ordinal).Take(count).Select(kv => kv.Value.Message).ToList();
    }

    public IReadOnlyList<StoredMessage> Find(string roomKey, IReadOnlyCollection<string> keys)
    {
        lock (_gate) { var room = Room(roomKey); return keys.Distinct().Where(room.ContainsKey).Select(k => room[k].Message).ToList(); }
    }

    public HashSet<string> Held(string roomKey, IReadOnlyCollection<string> keys)
    {
        lock (_gate) { var room = Room(roomKey); return keys.Where(room.ContainsKey).ToHashSet(); }
    }

    public IReadOnlyList<StoredMessage> ReactionsTo(string roomKey, IReadOnlyCollection<string> targets)
    {
        var wanted = targets.ToHashSet();
        lock (_gate) return Room(roomKey).Values.Where(v => v.Target != null && wanted.Contains(v.Target)).Select(v => v.Message).ToList();
    }

    public IReadOnlyList<StoredMessage> ByAuthors(string roomKey, IReadOnlyCollection<string> authors)
    {
        lock (_gate) return Room(roomKey).Values.Where(v => authors.Contains(v.Message.From)).Select(v => v.Message).ToList();
    }

    public IReadOnlyList<(string Key, StoredMessage Message)> Window(string roomKey, long sinceTs, int perChannel, int reactions)
    {
        lock (_gate)
        {
            var recent = Room(roomKey).Where(kv => kv.Value.Message.Ts >= sinceTs).ToList();
            var messages = recent.Where(kv => kv.Value.Target == null).GroupBy(kv => Channel(kv.Value.Message))
                .SelectMany(g => g.OrderByDescending(kv => kv.Value.Message.Ts).Take(perChannel));
            var reacted = recent.Where(kv => kv.Value.Target != null).OrderByDescending(kv => kv.Value.Message.Ts).Take(reactions);
            return messages.Concat(reacted).Select(kv => (kv.Key, kv.Value.Message)).ToList();
        }
    }

    public string[] WindowKeys(string roomKey, long sinceTs, int perChannel, int reactions) =>
        Window(roomKey, sinceTs, perChannel, reactions).Select(w => w.Key).ToArray();

    public IReadOnlyList<string> Rooms()
    {
        lock (_gate) return _rooms.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
    }

    public IReadOnlyDictionary<string, int> MonthCounts(string roomKey)
    {
        lock (_gate) return Room(roomKey).Values.GroupBy(v => HistoryMonths.Of(v.Message.Ts)).ToDictionary(g => g.Key, g => g.Count());
    }

    public IReadOnlyList<(string Key, StoredMessage Message, string? Target)> InMonth(string roomKey, string month)
    {
        var (from, to) = HistoryMonths.Range(month);
        lock (_gate) return Room(roomKey).Where(kv => kv.Value.Message.Ts >= from && kv.Value.Message.Ts < to).Select(kv => (kv.Key, kv.Value.Message, kv.Value.Target)).ToList();
    }

    public IReadOnlyDictionary<string, int> DirectMonthCounts(string peer)
    {
        lock (_gate) return Chat(peer).Values.GroupBy(v => HistoryMonths.Of(v.Record.Ts)).ToDictionary(g => g.Key, g => g.Count());
    }

    public IReadOnlyList<(string Key, DirectRecord Record, string? Target)> DirectInMonth(string peer, string month)
    {
        var (from, to) = HistoryMonths.Range(month);
        lock (_gate) return Chat(peer).Where(kv => kv.Value.Record.Ts >= from && kv.Value.Record.Ts < to).Select(kv => (kv.Key, kv.Value.Record, kv.Value.Target)).ToList();
    }
}

/// <summary>The files history was kept in before it moved into a database. They are only read now, to bring them over (see <see cref="Load"/>).</summary>
public sealed class FileHistoryStore
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

/// <summary>Brings the history files of earlier versions (one file for each room) into a store.</summary>
public static class HistoryImport
{
    /// <summary>
    /// Reads every room's file in <paramref name="directory"/>, keeps its messages in <paramref name="store"/> and deletes the file once they are there.
    /// Messages that do not check out are left behind. Returns how many messages were brought over.
    /// </summary>
    public static int FromFiles(IChatHistoryStore store, string directory, Yaparr.Identity.HistoryCipher? cipher, Action<string>? log = null)
    {
        if (!Directory.Exists(directory)) return 0;
        var files = new FileHistoryStore(directory, cipher);
        int total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl").ToList())
        {
            var room = Path.GetFileNameWithoutExtension(path);
            try
            {
                var loaded = files.Load(room);
                var good = loaded.Where(m => SignedMessage.Verify(m, room)).Select(m =>
                    (SignedMessage.Key(m), m, MessageEnvelope.TryReadReaction(m.Text, out var reaction) ? reaction.Target : MessageEnvelope.IsReactionText(m.Text) ? "" : null)).ToList();
                int added = store.AddMany(room, good);
                total += added;
                log?.Invoke($"history of room {room[..Math.Min(8, room.Length)]}: {added} message(s) brought over ({loaded.Count - good.Count} dropped)");
                File.Delete(path);
            }
            catch (IOException ex) { log?.Invoke($"could not bring over {Path.GetFileName(path)}: {ex.Message}"); }
        }
        return total;
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

/// <summary>
/// A room's messages. All of them are kept (in the <see cref="IChatHistoryStore"/>) and nothing is read whole: a device shows the newest ones first and
/// reads earlier ones as they are scrolled to, and what it hands to other members, and compares with theirs, is a window: the messages of the last
/// <c>maxAge</c>, at most <c>max</c> of the newest for each channel (the limits history had when they were all that was kept).
/// </summary>
internal sealed class RoomHistory
{
    private readonly string _roomKey;
    private readonly IChatHistoryStore _store;
    private readonly int _max;
    private readonly TimeSpan _maxAge;

    public RoomHistory(string roomKey, IChatHistoryStore? store, int max, TimeSpan maxAge)
    {
        _roomKey = roomKey;
        _store = store ?? new MemoryHistoryStore();
        _max = max;
        _maxAge = maxAge;
    }

    /// <summary>False if it was already here.</summary>
    public bool TryAdd(StoredMessage m)
    {
        string? target = MessageEnvelope.TryReadReaction(m.Text, out var reaction) ? reaction.Target : MessageEnvelope.IsReactionText(m.Text) ? "" : null;
        return _store.Add(_roomKey, SignedMessage.Key(m), m, target);
    }

    /// <summary>Forgets messages (by key), here and in the store, so they are not served to anyone else either.</summary>
    public void Remove(IEnumerable<string> keys)
    {
        var list = keys as IReadOnlyCollection<string> ?? keys.ToList();
        if (list.Count > 0) _store.Remove(_roomKey, list);
    }

    /// <summary>The newest messages of each channel (reactions are not messages), oldest first.</summary>
    public List<StoredMessage> Newest(int perChannel) =>
        _store.Newest(_roomKey, perChannel).OrderBy(m => m.Ts).ThenBy(SignedMessage.Key, StringComparer.Ordinal).ToList();

    /// <summary>Messages of a channel (null for the first) before this one, newest first.</summary>
    public IReadOnlyList<StoredMessage> Before(string? channel, long ts, string key, int count) => _store.Before(_roomKey, channel, ts, key, count);

    /// <summary>The messages held for these keys, oldest first (keys that are not held are left out).</summary>
    public List<StoredMessage> Find(IEnumerable<string> keys)
    {
        var list = keys.Distinct().ToList();
        return list.Count == 0 ? new() : _store.Find(_roomKey, list).OrderBy(m => m.Ts).ToList();
    }

    public HashSet<string> Held(IEnumerable<string> keys)
    {
        var list = keys.Distinct().ToList();
        return list.Count == 0 ? new() : _store.Held(_roomKey, list);
    }

    public IReadOnlyList<StoredMessage> ReactionsTo(IReadOnlyCollection<string> targets) =>
        targets.Count == 0 ? Array.Empty<StoredMessage>() : _store.ReactionsTo(_roomKey, targets);

    /// <summary>Whether any message in the store (of any room or conversation) mentions this text.</summary>
    public bool Mentions(string text) => _store.Mentions(text);

    public IReadOnlyList<StoredMessage> ByAuthors(IReadOnlyCollection<string> authors) =>
        authors.Count == 0 ? Array.Empty<StoredMessage>() : _store.ByAuthors(_roomKey, authors);

    private long Since() => DateTimeOffset.UtcNow.Subtract(_maxAge).ToUnixTimeMilliseconds();

    /// <summary>How many reactions are shared for each message the window can hold: they are small, and a busy room reacts a lot.</summary>
    public const int ReactionBudget = 4;

    /// <summary>The keys of the window that is shared with other members.</summary>
    public string[] Keys() => _store.WindowKeys(_roomKey, Since(), _max, _max * ReactionBudget);

    /// <summary>Messages of the shared window that are not among <paramref name="have"/>: the newest <paramref name="cap"/>, oldest first.</summary>
    public List<StoredMessage> Missing(HashSet<string> have, int cap) =>
        _store.Window(_roomKey, Since(), _max, _max * ReactionBudget).Where(w => !have.Contains(w.Key)).Select(w => w.Message)
            .OrderByDescending(m => m.Ts).Take(cap).OrderBy(m => m.Ts).ToList();
}
