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
/// The direct messages of each conversation, kept for the same time and number as a room's history, so that a device
/// that was off, or is new, can be given the conversations it missed by another of your devices.
/// </summary>
internal sealed class DirectHistory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, DirectRecord>> _chats = new();
    private readonly string? _directory;
    private readonly HistoryCipher? _cipher;
    private readonly int _max;
    private readonly TimeSpan _maxAge;

    public DirectHistory(string? directory, int max, TimeSpan maxAge, HistoryCipher? cipher = null)
    {
        _directory = directory;
        _cipher = cipher;
        _max = max;
        _maxAge = maxAge;
        if (directory == null || !Directory.Exists(directory)) return;

        foreach (var file in Directory.EnumerateFiles(directory, "dm-*.jsonl"))
        {
            int lines = 0;
            bool plain = false;
            string? peer = null;
            try
            {
                foreach (var stored in File.ReadLines(file))
                {
                    lines++;
                    var line = Unseal(stored, file, out bool wasPlain);
                    plain |= wasPlain;
                    if (line == null) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<DirectRecord>(line, Json) is { } r && IsValid(r) && r.Ts >= OldestAllowed())
                        {
                            peer = r.Peer;
                            Chat(r.Peer)[Key(r)] = r;
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { continue; }

            // A file whose lines cannot all be opened (another key) is left alone; it may be opened again with the right one.
            if (peer == null) { if (lines == 0) TryDelete(file); continue; }
            Trim(peer);
            // Rewritten when lines were dropped, or when they were written before history was sealed.
            if (_chats[peer].Count != lines || plain && _cipher != null) Rewrite(peer);
        }
    }

    public static bool IsValid(DirectRecord r) =>
        r.Id is { Length: > 0 and <= 64 } && r.Peer is { Length: 64 } && r.Peer.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
        && r.Name is { Length: <= 64 } && r.Text is { Length: <= 16_000 }
        && r.Pub is { Length: <= 200 } && r.Acct is { Length: <= 200 } && r.Cert is { Length: <= 200 } && r.Sig is { Length: <= 200 };

    /// <summary>Identifies a message the same way on every device.</summary>
    public static string Key(DirectRecord r) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{(r.Mine ? "o" : "i")}:{r.Peer}:{r.Id}")))[..16].ToLowerInvariant();

    /// <summary>False if it was already here or is older than is kept.</summary>
    public bool TryAdd(DirectRecord r)
    {
        lock (_gate)
        {
            if (r.Ts < OldestAllowed()) return false;
            if (!Chat(r.Peer).TryAdd(Key(r), r)) return false;
            Append(r);
            Trim(r.Peer);
            return true;
        }
    }

    public IReadOnlyList<DirectRecord> For(string peer)
    {
        lock (_gate)
        {
            if (!_chats.ContainsKey(peer)) return Array.Empty<DirectRecord>();
            Trim(peer);
            return _chats[peer].Values.OrderBy(r => r.Ts).ThenBy(Key, StringComparer.Ordinal).ToList();
        }
    }

    public IReadOnlyList<string> Peers()
    {
        lock (_gate) return _chats.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
    }

    public string LastName(string peer)
    {
        lock (_gate)
            return _chats.TryGetValue(peer, out var chat) ? chat.Values.Where(r => !r.Mine && r.Name.Length > 0).OrderByDescending(r => r.Ts).Select(r => r.Name).FirstOrDefault() ?? "" : "";
    }

    /// <summary>The newest messages across all conversations, up to <paramref name="cap"/>.</summary>
    public string[] Keys(int cap)
    {
        lock (_gate)
            return _chats.Values.SelectMany(c => c).OrderByDescending(kv => kv.Value.Ts).Take(cap).Select(kv => kv.Key).ToArray();
    }

    public List<DirectRecord> Missing(HashSet<string> have, int cap)
    {
        lock (_gate)
            return _chats.Values.SelectMany(c => c).Where(kv => !have.Contains(kv.Key)).Select(kv => kv.Value)
                .OrderByDescending(r => r.Ts).Take(cap).OrderBy(r => r.Ts).ToList();
    }

    private Dictionary<string, DirectRecord> Chat(string peer)
    {
        if (!_chats.TryGetValue(peer, out var chat)) _chats[peer] = chat = new();
        return chat;
    }

    private long OldestAllowed() => DateTimeOffset.UtcNow.Subtract(_maxAge).ToUnixTimeMilliseconds();

    private void Trim(string peer)
    {
        var chat = _chats[peer];
        long cutoff = OldestAllowed();
        foreach (var k in chat.Where(kv => kv.Value.Ts < cutoff).Select(kv => kv.Key).ToList()) chat.Remove(k);
        if (chat.Count <= _max) return;
        foreach (var k in chat.OrderBy(kv => kv.Value.Ts).Take(chat.Count - _max).Select(kv => kv.Key).ToList()) chat.Remove(k);
    }

    private string? PathFor(string peer) => _directory == null ? null : Path.Combine(_directory, $"dm-{peer}.jsonl");

    private string Seal(string line, string path) => _cipher == null ? line : _cipher.Seal(line, Path.GetFileName(path));

    private string? Unseal(string line, string path, out bool wasPlain)
    {
        wasPlain = false;
        return _cipher == null ? line : _cipher.Open(line, Path.GetFileName(path), out wasPlain);
    }

    private void Append(DirectRecord r)
    {
        if (PathFor(r.Peer) is not { } path) return;
        try
        {
            Directory.CreateDirectory(_directory!);
            File.AppendAllText(path, Seal(JsonSerializer.Serialize(r, Json), path) + "\n");
        }
        catch (IOException) { }
    }

    private void Rewrite(string peer)
    {
        if (PathFor(peer) is not { } path) return;
        try
        {
            var temp = path + ".tmp";
            File.WriteAllLines(temp, _chats[peer].Values.OrderBy(r => r.Ts).Select(r => Seal(JsonSerializer.Serialize(r, Json), path)));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
