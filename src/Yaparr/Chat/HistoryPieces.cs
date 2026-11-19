using System.IO.Compression;
using System.Text.Json;

namespace Yaparr.Chat;

/// <summary>
/// History as it is kept in sync: one <b>piece</b> for each month of each room or direct conversation (UTC), named <c>h-&lt;room&gt;-&lt;yyyyMM&gt;</c> or <c>d-&lt;person&gt;-&lt;yyyyMM&gt;</c>, holding
/// every row of that month (messages and reactions) as the store has them, compressed. A piece is built from a store and taken into a store (what is already there is left alone, so
/// taking is safe in any order and any number of times). A piece's <em>tag</em> is how many rows it has: a device whose month has a different count than what the account holds has
/// something to write. Nothing is trusted because of where it came from: a room's message has to carry a good signature for that room, and a direct message has to be one that
/// belongs to that conversation and month.
/// </summary>
public static class HistoryPieces
{
    private const int MaxRaw = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record RoomRow(string K, StoredMessage M, string? T);
    private sealed record DirectRow(string K, DirectRecord R, string? T);
    private sealed record Piece(int V, List<RoomRow>? Rooms, List<DirectRow>? Direct);

    private static bool Safe(string id) => id.Length is > 0 and <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '@' or '+' or '=');

    public static string RoomName(string room, string month) => $"h-{room}-{month}";

    public static string DirectName(string peer, string month) => $"d-{peer}-{month}";

    private static bool TryParse(string name, out bool direct, out string who, out string month)
    {
        direct = false; who = month = "";
        if (name.Length < 10 || name[1] != '-' || name[^7] != '-' || name[0] is not ('h' or 'd')) return false;
        direct = name[0] == 'd';
        who = name[2..^7];
        month = name[^6..];
        return Safe(who) && HistoryMonths.IsMonth(month);
    }

    /// <summary>The pieces this store has, by name, each with its tag.</summary>
    public static Dictionary<string, string> Local(IChatHistoryStore store)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var room in store.Rooms())
            if (Safe(room))
                foreach (var (month, count) in store.MonthCounts(room)) all[RoomName(room, month)] = count.ToString();
        if (store is IDirectHistoryStore direct)
            foreach (var peer in direct.DirectPeers())
                if (Safe(peer))
                    foreach (var (month, count) in direct.DirectMonthCounts(peer)) all[DirectName(peer, month)] = count.ToString();
        return all;
    }

    /// <summary>The piece with this name, or null when the store has nothing of it.</summary>
    public static byte[]? Build(IChatHistoryStore store, string name)
    {
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
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gzip, piece, Json);
        return output.ToArray();
    }

    /// <summary>Takes a piece into the store. Returns how many rows were new, or -1 if it is not a good piece of that name.</summary>
    public static int Take(IChatHistoryStore store, string name, byte[] content)
    {
        if (!TryParse(name, out var direct, out var who, out var month)) return -1;
        Piece? piece;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer)) > 0)
            {
                if (raw.Length + read > MaxRaw) return -1;
                raw.Write(buffer, 0, read);
            }
            piece = JsonSerializer.Deserialize<Piece>(raw.ToArray(), Json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return -1; }
        if (piece is not { V: 1 }) return -1;

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
