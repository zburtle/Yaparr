using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// One direct message, as kept on a device and passed to the person's other devices. <see cref="Peer"/> is the other
/// person's account; <see cref="Mine"/> says whether it was sent by you (from any of your devices) or to you.
/// <see cref="Pub"/>, <see cref="Acct"/>, <see cref="Cert"/> and <see cref="Sig"/> prove who wrote it (they are empty on
/// a message from an older version, which stays on this device), and <see cref="St"/> is the time the author signed,
/// which <see cref="Ts"/> can differ from once clocks are corrected.
/// </summary>
public sealed record DirectRecord(string Id, string Peer, string Name, bool Mine, long Ts, string Text,
    string Pub = "", string Acct = "", string Cert = "", string Sig = "", long St = 0);

/// <summary>
/// Where direct messages are kept: for good, a conversation for each person. As with <see cref="IChatHistoryStore"/> nothing is read whole; keys are
/// <see cref="DirectHistory.Key"/>, and a reaction's target is the key of the message it is on.
/// </summary>
public interface IDirectHistoryStore
{
    /// <summary>Keeps a message. False when the conversation already has it.</summary>
    bool AddDirect(string key, DirectRecord record, string? reactionTarget);

    /// <summary>Keeps many at once (bringing history over); those already held are left alone. Returns how many were new.</summary>
    int AddDirectMany(IEnumerable<(string Key, DirectRecord Record, string? Target)> records)
    {
        int added = 0;
        foreach (var (key, record, target) in records) if (AddDirect(key, record, target)) added++;
        return added;
    }

    /// <summary>The newest <paramref name="count"/> messages (not reactions) of a conversation, in no particular order.</summary>
    IReadOnlyList<DirectRecord> DirectNewest(string peer, int count);

    /// <summary>Up to <paramref name="count"/> messages (not reactions) older than the one at <paramref name="ts"/> with key <paramref name="key"/>, newest first.</summary>
    IReadOnlyList<DirectRecord> DirectBefore(string peer, long ts, string key, int count);

    /// <summary>The message with this key, if the conversation has it.</summary>
    DirectRecord? DirectFind(string peer, string key);

    /// <summary>The reactions on the messages with these keys.</summary>
    IReadOnlyList<DirectRecord> DirectReactionsTo(string peer, IReadOnlyCollection<string> targets);

    /// <summary>The message you sent with this id, if the conversation has it.</summary>
    DirectRecord? DirectOwn(string peer, string id);

    /// <summary>Whether there is anything kept for this person.</summary>
    bool DirectAny(string peer);

    /// <summary>The time of the newest message from them (0 if none).</summary>
    long DirectNewestIncoming(string peer);

    /// <summary>The people there are conversations with.</summary>
    IReadOnlyList<string> DirectPeers();

    /// <summary>The last name they used.</summary>
    string DirectLastName(string peer);

    /// <summary>
    /// The window handed to your other devices: for each conversation the messages not older than <paramref name="sinceTs"/>, at most <paramref name="perChat"/>
    /// of the newest and <paramref name="reactions"/> of the newest reactions; the newest <paramref name="cap"/> of those altogether, newest first.
    /// </summary>
    IReadOnlyList<(string Key, DirectRecord Record)> DirectWindow(long sinceTs, int perChat, int reactions, int cap);

    /// <summary>For each month (<see cref="HistoryMonths"/>) a conversation has anything from: how many rows it has (messages and reactions).</summary>
    IReadOnlyDictionary<string, int> DirectMonthCounts(string peer);

    /// <summary>Every row (message or reaction, with the key of what the reaction is on) of one month of a conversation.</summary>
    IReadOnlyList<(string Key, DirectRecord Record, string? Target)> DirectInMonth(string peer, string month);
}

/// <summary>
/// The direct messages of each conversation. All of them are kept; a device that was off, or is new, can be given the conversations it missed by another
/// of your devices from the window of the last <c>maxAge</c> (at most <c>max</c> messages of each conversation), the same limits room history has.
/// </summary>
internal sealed class DirectHistory
{
    private readonly IDirectHistoryStore _store;
    private readonly int _max;
    private readonly TimeSpan _maxAge;

    public DirectHistory(IDirectHistoryStore? store, int max, TimeSpan maxAge)
    {
        _store = store ?? new MemoryHistoryStore();
        _max = max;
        _maxAge = maxAge;
    }

    public static bool IsValid(DirectRecord r) =>
        r.Id is { Length: > 0 and <= 64 } && r.Peer is { Length: 64 } && r.Peer.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
        && r.Name is { Length: <= 64 } && r.Text is { Length: <= 16_000 }
        && r.Pub is { Length: <= 200 } && r.Acct is { Length: <= 200 } && r.Cert is { Length: <= 200 } && r.Sig is { Length: <= 200 };

    /// <summary>Identifies a message the same way on every device.</summary>
    public static string Key(DirectRecord r) => KeyFor(r.Peer, r.Id, r.Mine);

    public static string KeyFor(string peer, string id, bool mine) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{(mine ? "o" : "i")}:{peer}:{id}")))[..16].ToLowerInvariant();

    public static string? ReactionTargetOf(string text) =>
        MessageEnvelope.TryReadReaction(text, out var reaction) ? reaction.Target : MessageEnvelope.IsReactionText(text) ? "" : null;

    /// <summary>False if it was already here.</summary>
    public bool TryAdd(DirectRecord r) => _store.AddDirect(Key(r), r, ReactionTargetOf(r.Text));

    /// <summary>The newest messages of a conversation (reactions are not messages), oldest first.</summary>
    public List<DirectRecord> Newest(string peer, int count) =>
        _store.DirectNewest(peer, count).OrderBy(r => r.Ts).ThenBy(Key, StringComparer.Ordinal).ToList();

    /// <summary>Messages before this one, newest first.</summary>
    public IReadOnlyList<DirectRecord> Before(string peer, long ts, string key, int count) => _store.DirectBefore(peer, ts, key, count);

    public DirectRecord? Find(string peer, string key) => _store.DirectFind(peer, key);

    /// <summary>The message with this id, whether you sent it or they did.</summary>
    public DirectRecord? FindById(string peer, string id) => _store.DirectFind(peer, KeyFor(peer, id, true)) ?? _store.DirectFind(peer, KeyFor(peer, id, false));

    public IReadOnlyList<DirectRecord> ReactionsTo(string peer, IReadOnlyCollection<string> targets) =>
        targets.Count == 0 ? Array.Empty<DirectRecord>() : _store.DirectReactionsTo(peer, targets);

    public DirectRecord? Own(string peer, string id) => _store.DirectOwn(peer, id);

    public bool Any(string peer) => _store.DirectAny(peer);

    /// <summary>Whether any message kept in the store (of any room or conversation) mentions this text.</summary>
    public bool Mentions(string text) => (_store as IChatHistoryStore)?.Mentions(text) ?? false;

    public long NewestIncoming(string peer) => _store.DirectNewestIncoming(peer);

    public IReadOnlyList<string> Peers() => _store.DirectPeers();

    public string LastName(string peer) => _store.DirectLastName(peer);

    private long Since() => DateTimeOffset.UtcNow.Subtract(_maxAge).ToUnixTimeMilliseconds();

    /// <summary>The newest messages across all conversations, up to <paramref name="cap"/>.</summary>
    public string[] Keys(int cap) =>
        _store.DirectWindow(Since(), _max, _max * RoomHistory.ReactionBudget, cap).Select(w => w.Key).ToArray();

    public List<DirectRecord> Missing(HashSet<string> have, int cap) =>
        _store.DirectWindow(Since(), _max, _max * RoomHistory.ReactionBudget, int.MaxValue).Where(w => !have.Contains(w.Key)).Select(w => w.Record)
            .OrderByDescending(r => r.Ts).Take(cap).OrderBy(r => r.Ts).ToList();
}

/// <summary>Reads the direct history files of earlier versions (one file for each conversation, <c>dm-*.jsonl</c>) so they can be brought into a store.</summary>
public static class DirectHistoryImport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Brings every conversation's file in <paramref name="directory"/> into <paramref name="store"/> and deletes the file. A file with lines that cannot be
    /// opened (another key) is left alone: it may be opened again with the right one. Returns how many messages were brought over.
    /// </summary>
    public static int FromFiles(IDirectHistoryStore store, string directory, HistoryCipher? cipher, Action<string>? log = null)
    {
        if (!Directory.Exists(directory)) return 0;
        int total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "dm-*.jsonl").ToList())
        {
            try
            {
                var records = new List<DirectRecord>();
                bool allOpened = true;
                int lines = 0;
                foreach (var stored in File.ReadLines(file))
                {
                    lines++;
                    var line = cipher == null ? stored : cipher.Open(stored, Path.GetFileName(file), out _);
                    if (line == null) { allOpened = false; continue; }
                    try { if (JsonSerializer.Deserialize<DirectRecord>(line, Json) is { } r && DirectHistory.IsValid(r)) records.Add(r); }
                    catch (JsonException) { }
                }
                if (!allOpened) { log?.Invoke($"{Path.GetFileName(file)} has lines that cannot be opened with this key; left alone"); continue; }
                int added = store.AddDirectMany(records.Select(r => (DirectHistory.Key(r), r, DirectHistory.ReactionTargetOf(r.Text))).ToList());
                total += added;
                log?.Invoke($"direct history {Path.GetFileName(file)[3..11]}: {added} message(s) brought over");
                File.Delete(file);
            }
            catch (IOException ex) { log?.Invoke($"could not bring over {Path.GetFileName(file)}: {ex.Message}"); }
        }
        return total;
    }
}
