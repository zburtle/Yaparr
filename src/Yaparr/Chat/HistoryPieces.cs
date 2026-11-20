using System.IO.Compression;
using System.Text.Json;

namespace Yaparr.Chat;

/// <summary>
/// History as it is kept in sync: one <b>piece</b> for each month of each room or direct conversation (UTC), named <c>h-&lt;room&gt;-&lt;yyyyMM&gt;</c> or <c>d-&lt;person&gt;-&lt;yyyyMM&gt;</c>, holding
/// every row of that month (messages and reactions) as the store has them, compressed. A piece is built from a store and taken into a store (what is already there is left alone, so
/// taking is safe in any order and any number of times). A piece's <em>tag</em> is how many rows it has: a device whose month has a different count than what the account holds has
/// something to write. Nothing is trusted because of where it came from: a room's message has to carry a good signature for that room, and a direct message has to be one that
/// belongs to that conversation and month.
/// <para>
/// What was <b>removed</b> travels as well, so it stays removed: a room has one more piece, <c>x-&lt;room&gt;</c>, with the keys of its removed messages (<see cref="IChatHistoryStore.ApplyRemoved"/>).
/// Taking it in deletes those messages here, and the store never keeps a removed message again, so a month written by a device that had not heard of the removal cannot bring it back,
/// in whatever order the pieces are taken in.
/// </para>
/// </summary>
public static class HistoryPieces
{
    private const int MaxRaw = 64 * 1024 * 1024;

    /// <summary>
    /// A month with more rows than this is kept as a piece for each day instead of one piece (a piece is at most about 15 MiB locked, and a piece that would not fit was left out). A
    /// row is a few hundred bytes and compresses well, so this is far below the size that would not fit. (<see cref="Local"/> takes another number, for tests.)
    /// </summary>
    internal const int MaxRowsPerPiece = 100_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record RoomRow(string K, StoredMessage M, string? T);
    private sealed record DirectRow(string K, DirectRecord R, string? T);
    private sealed record Piece(int V, List<RoomRow>? Rooms, List<DirectRow>? Direct);
    private sealed record RemovedPiece(int V, List<string> Keys);

    private const string RemovedPrefix = "x-";

    public static string RemovedName(string room) => RemovedPrefix + room;

    private static bool IsMessageKey(string key) => key.Length == 32 && key.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static byte[] Pack<T>(T value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, value, Json);
        return output.ToArray();
    }

    private static T? Unpack<T>(byte[] content) where T : class
    {
        try
        {
            using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                if (raw.Length + read > MaxRaw) return null;
                raw.Write(buffer, 0, read);
            }
            return JsonSerializer.Deserialize<T>(raw.ToArray(), Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return null; }
    }

    private static bool Safe(string id) => id.Length is > 0 and <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '@' or '+' or '=');

    public static string RoomName(string room, string month) => $"h-{room}-{month}";

    public static string DirectName(string peer, string month) => $"d-{peer}-{month}";

    private static bool TryParse(string name, out bool direct, out string who, out string month)
    {
        direct = false; who = month = "";
        if (name.Length < 10 || name[1] != '-' || name[0] is not ('h' or 'd')) return false;
        // The end is a month (yyyyMM) or, for a month with too many rows for one piece, a day of it (yyyyMMdd).
        int length = name.Length > 12 && name[^9] == '-' ? 8 : 6;
        if (name[^(length + 1)] != '-') return false;
        direct = name[0] == 'd';
        who = name[2..^(length + 1)];
        month = name[^length..];
        return Safe(who) && HistoryMonths.IsPeriod(month);
    }

    /// <summary>Whether a piece is about a person's conversation (true) or a room (false), and which.</summary>
    public static (bool Direct, string Who)? WhoOf(string name)
    {
        if (name.StartsWith(RemovedPrefix, StringComparison.Ordinal)) return Safe(name[RemovedPrefix.Length..]) ? (false, name[RemovedPrefix.Length..]) : null;
        return TryParse(name, out var direct, out var who, out _) ? (direct, who) : null;
    }

    /// <summary>The pieces this store has, by name, each with its tag.</summary>
    public static Dictionary<string, string> Local(IChatHistoryStore store, int maxRows = MaxRowsPerPiece)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var room in store.Rooms())
            if (Safe(room))
                foreach (var (month, count) in store.MonthCounts(room))
                {
                    if (count <= maxRows) all[RoomName(room, month)] = count.ToString();
                    else foreach (var (day, n) in store.DayCounts(room, month)) all[RoomName(room, day)] = n.ToString();
                }
        foreach (var room in store.RoomsWithRemoved())
            if (Safe(room)) all[RemovedName(room)] = store.RemovedKeys(room).Count.ToString();
        if (store is IDirectHistoryStore direct)
            foreach (var peer in direct.DirectPeers())
                if (Safe(peer))
                    foreach (var (month, count) in direct.DirectMonthCounts(peer))
                    {
                        if (count <= maxRows) all[DirectName(peer, month)] = count.ToString();
                        else foreach (var (day, n) in direct.DirectDayCounts(peer, month)) all[DirectName(peer, day)] = n.ToString();
                    }
        return all;
    }

    /// <summary>The piece with this name, or null when the store has nothing of it.</summary>
    public static byte[]? Build(IChatHistoryStore store, string name)
    {
        if (name.StartsWith(RemovedPrefix, StringComparison.Ordinal))
        {
            var keys = Safe(name[RemovedPrefix.Length..]) ? store.RemovedKeys(name[RemovedPrefix.Length..]) : new List<string>();
            return keys.Count == 0 ? null : Pack(new RemovedPiece(1, keys.ToList()));
        }
        if (!TryParse(name, out var direct, out var who, out var month)) return null;
        Piece piece;
        if (direct)
        {
            if (store is not IDirectHistoryStore d) return null;
            var rows = d.DirectInMonth(who, month);
            if (rows.Count == 0) return null;
            piece = new Piece(1, null, rows.Select(r => new DirectRow(r.Key, r.Record, r.Target)).ToList());
        }
        else
        {
            var rows = store.InMonth(who, month);
            if (rows.Count == 0) return null;
            piece = new Piece(1, rows.Select(r => new RoomRow(r.Key, r.Message, r.Target)).ToList(), null);
        }
        return Pack(piece);
    }

    /// <summary>Takes a piece into the store. Returns how many rows were new, or -1 if it is not a good piece of that name.</summary>
    public static int Take(IChatHistoryStore store, string name, byte[] content)
    {
        if (name.StartsWith(RemovedPrefix, StringComparison.Ordinal))
        {
            var room = name[RemovedPrefix.Length..];
            if (!Safe(room) || Unpack<RemovedPiece>(content) is not { V: 1, Keys: { } removed }) return -1;
            return store.ApplyRemoved(room, removed.Where(IsMessageKey).Distinct().ToList());
        }
        if (!TryParse(name, out var direct, out var who, out var month)) return -1;
        if (Unpack<Piece>(content) is not { V: 1 } piece) return -1;

        var (from, to) = HistoryMonths.Range(month);
        if (direct)
        {
            if (store is not IDirectHistoryStore d || piece.Direct == null) return -1;
            var good = new List<(string, DirectRecord, string?)>();
            foreach (var row in piece.Direct)
                if (row.R is { } r && r.Peer == who && r.Ts >= from && r.Ts < to && DirectHistory.IsValid(r))
                    good.Add((DirectHistory.Key(r), r, DirectHistory.ReactionTargetOf(r.Text)));
            return d.AddDirectMany(good);
        }
        if (piece.Rooms == null) return -1;
        var rows = new List<(string, StoredMessage, string?)>();
        foreach (var row in piece.Rooms)
            if (row.M is { } m && m.Ts >= from && m.Ts < to && SignedMessage.Verify(m, who))
                rows.Add((SignedMessage.Key(m), m, MessageEnvelope.TryReadReaction(m.Text, out var reaction) ? reaction.Target : MessageEnvelope.IsReactionText(m.Text) ? "" : null));
        return store.AddMany(who, rows);
    }
}
