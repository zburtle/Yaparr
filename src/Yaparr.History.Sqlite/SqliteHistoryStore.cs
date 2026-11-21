using System.Text;
using Microsoft.Data.Sqlite;
using Yaparr.Chat;

namespace Yaparr.History.Sqlite;

/// <summary>
/// The messages of every room, in one SQLite database that SQLCipher encrypts as a whole (the key comes from <see cref="Yaparr.Identity.HistoryCipher.DatabaseKey"/>),
/// so the file shows nothing of who said what or when. Everything is kept; nothing is read whole (see <see cref="IChatHistoryStore"/>).
/// </summary>
public sealed class SqliteHistoryStore : IChatHistoryStore, IDirectHistoryStore, IDisposable
{
    private const int Version = 3, Chunk = 400;

    static SqliteHistoryStore() => SQLitePCL.Batteries_V2.Init();

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    /// <param name="path">The database file, or null for one that lives in memory only.</param>
    /// <param name="key">The key of the database (32 bytes), or null to leave it unencrypted.</param>
    /// <exception cref="SqliteException">The file is not a database, or <paramref name="key"/> is not its key.</exception>
    public SqliteHistoryStore(string? path, byte[]? key = null)
    {
        if (path != null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? ":memory:", Pooling = false }.ToString());
        _db.Open();
        try
        {
            if (key != null)
            {
                if (key.Length != 32) throw new ArgumentException("The key must be 32 bytes.", nameof(key));
                Exec($"PRAGMA key = \"x'{Convert.ToHexString(key)}'\"");
            }
            // Reading anything fails here when the file is not a database, or the key is not its key.
            Scalar("SELECT count(*) FROM sqlite_master");
            Migrate();
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the database at <paramref name="path"/>. A file that cannot be opened with the key (the key was lost, or the file is from another
    /// device) is kept aside as <c>.unreadable</c> and a new database is made, as an unreadable key file is.
    /// </summary>
    public static SqliteHistoryStore Open(string path, byte[]? key, Action<string>? log = null)
    {
        try { return new SqliteHistoryStore(path, key); }
        catch (SqliteException ex) when (File.Exists(path))
        {
            log?.Invoke($"the history database could not be opened ({ex.Message}); keeping it aside and starting a new one");
            File.Move(path, path + ".unreadable", overwrite: true);
            return new SqliteHistoryStore(path, key);
        }
    }

    private void Migrate()
    {
        long version = Convert.ToInt64(Scalar("PRAGMA user_version"));
        if (version >= Version) return;
        Exec("""
            CREATE TABLE IF NOT EXISTS direct(
                peer TEXT NOT NULL, key TEXT NOT NULL, id TEXT NOT NULL, name TEXT NOT NULL, mine INTEGER NOT NULL, ts INTEGER NOT NULL, body TEXT NOT NULL,
                pub TEXT NOT NULL, acct TEXT NOT NULL, cert TEXT NOT NULL, sig TEXT NOT NULL, st INTEGER NOT NULL, target TEXT,
                PRIMARY KEY(peer, key)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS direct_by_time ON direct(peer, ts, key) WHERE target IS NULL;
            CREATE INDEX IF NOT EXISTS direct_by_target ON direct(peer, target) WHERE target IS NOT NULL;
            CREATE INDEX IF NOT EXISTS direct_by_id ON direct(peer, id);
            CREATE TABLE IF NOT EXISTS messages(
                room TEXT NOT NULL, key TEXT NOT NULL, author TEXT NOT NULL, pub TEXT NOT NULL, name TEXT NOT NULL, id TEXT NOT NULL,
                ts INTEGER NOT NULL, body TEXT NOT NULL, sig TEXT NOT NULL, chan TEXT NOT NULL DEFAULT '', target TEXT,
                PRIMARY KEY(room, key)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS messages_by_time ON messages(room, chan, ts, key) WHERE target IS NULL;
            CREATE INDEX IF NOT EXISTS messages_by_target ON messages(room, target) WHERE target IS NOT NULL;
            CREATE INDEX IF NOT EXISTS messages_by_author ON messages(room, author);
            CREATE INDEX IF NOT EXISTS messages_by_ts ON messages(room, ts);
            CREATE TABLE IF NOT EXISTS removed(room TEXT NOT NULL, key TEXT NOT NULL, PRIMARY KEY(room, key)) WITHOUT ROWID;
            """);
        Exec($"PRAGMA user_version = {Version}");
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private const string Columns = "author, pub, name, id, ts, body, sig, chan";

    private static StoredMessage Read(SqliteDataReader r, int at = 0) =>
        new(r.GetString(at), r.GetString(at + 1), r.GetString(at + 2), r.GetString(at + 3), r.GetInt64(at + 4), r.GetString(at + 5), r.GetString(at + 6),
            r.GetString(at + 7) is { Length: > 0 } chan ? chan : null);

    // A message that was removed is not kept again: the insert is skipped for a key the room has a record of removing.
    private const string InsertMessage =
        "INSERT OR IGNORE INTO messages(room, key, author, pub, name, id, ts, body, sig, chan, target) " +
        "SELECT $r, $k, $a, $p, $n, $i, $t, $b, $s, $c, $g WHERE NOT EXISTS (SELECT 1 FROM removed WHERE room = $r AND key = $k)";

    public bool Add(string roomKey, string key, StoredMessage m, string? reactionTarget)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = InsertMessage;
            cmd.Parameters.AddWithValue("$r", roomKey);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$a", m.From);
            cmd.Parameters.AddWithValue("$p", m.Pub);
            cmd.Parameters.AddWithValue("$n", m.Name);
            cmd.Parameters.AddWithValue("$i", m.Id);
            cmd.Parameters.AddWithValue("$t", m.Ts);
            cmd.Parameters.AddWithValue("$b", m.Text);
            cmd.Parameters.AddWithValue("$s", m.Sig);
            cmd.Parameters.AddWithValue("$c", m.Chan ?? "");
            cmd.Parameters.AddWithValue("$g", (object?)reactionTarget ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Keeps many messages at once, in one transaction (bringing the old files over); those already held are left alone.</summary>
    public int AddMany(string roomKey, IEnumerable<(string Key, StoredMessage Message, string? Target)> messages)
    {
        lock (_gate)
        {
            int added = 0;
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = InsertMessage;
            var names = new[] { "$r", "$k", "$a", "$p", "$n", "$i", "$t", "$b", "$s", "$c", "$g" };
            foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, null));
            foreach (var (key, m, target) in messages)
            {
                cmd.Parameters["$r"].Value = roomKey;
                cmd.Parameters["$k"].Value = key;
                cmd.Parameters["$a"].Value = m.From;
                cmd.Parameters["$p"].Value = m.Pub;
                cmd.Parameters["$n"].Value = m.Name;
                cmd.Parameters["$i"].Value = m.Id;
                cmd.Parameters["$t"].Value = m.Ts;
                cmd.Parameters["$b"].Value = m.Text;
                cmd.Parameters["$s"].Value = m.Sig;
                cmd.Parameters["$c"].Value = m.Chan ?? "";
                cmd.Parameters["$g"].Value = (object?)target ?? DBNull.Value;
                added += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return added;
        }
    }

    public void Remove(string roomKey, IReadOnlyCollection<string> keys)
    {
        lock (_gate)
            foreach (var part in keys.Chunk(Chunk))
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = $"DELETE FROM messages WHERE room = $r AND key IN ({Marks(part.Length)})";
                cmd.Parameters.AddWithValue("$r", roomKey);
                for (int i = 0; i < part.Length; i++) cmd.Parameters.AddWithValue("$" + i, part[i]);
                cmd.ExecuteNonQuery();
            }
    }

    public int ApplyRemoved(string roomKey, IReadOnlyCollection<string> keys)
    {
        lock (_gate)
        {
            int added = 0;
            using var tx = _db.BeginTransaction();
            foreach (var part in keys.Distinct().Chunk(Chunk))
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    // The removals of a direct conversation ("d:<person>") take its messages out of the conversation's table.
                    bool direct = roomKey.StartsWith(DirectRemovals.Prefix, StringComparison.Ordinal);
                    cmd.CommandText = direct
                        ? $"DELETE FROM direct WHERE peer = $r AND key IN ({Marks(part.Length)})"
                        : $"DELETE FROM messages WHERE room = $r AND key IN ({Marks(part.Length)})";
                    cmd.Parameters.AddWithValue("$r", direct ? roomKey[DirectRemovals.Prefix.Length..] : roomKey);
                    for (int i = 0; i < part.Length; i++) cmd.Parameters.AddWithValue("$" + i, part[i]);
                    cmd.ExecuteNonQuery();
                }
                using var note = _db.CreateCommand();
                note.Transaction = tx;
                note.CommandText = "INSERT OR IGNORE INTO removed(room, key) VALUES($r, $k)";
                note.Parameters.AddWithValue("$r", roomKey);
                var key = new SqliteParameter("$k", null);
                note.Parameters.Add(key);
                foreach (var k in part) { key.Value = k; added += note.ExecuteNonQuery(); }
            }
            tx.Commit();
            return added;
        }
    }

    public IReadOnlyList<string> RemovedKeys(string roomKey)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT key FROM removed WHERE room = $r ORDER BY key";
            cmd.Parameters.AddWithValue("$r", roomKey);
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public IReadOnlyList<string> RoomsWithRemoved()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT room FROM removed";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    private static string Marks(int count) => string.Join(",", Enumerable.Range(0, count).Select(i => "$" + i));

    private List<StoredMessage> Query(string sql, Action<SqliteCommand> bind)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            using var r = cmd.ExecuteReader();
            var list = new List<StoredMessage>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    public bool Mentions(string text)
    {
        lock (_gate)
        {
            foreach (var sql in new[] { "SELECT 1 FROM messages WHERE instr(body, $t) > 0 LIMIT 1", "SELECT 1 FROM direct WHERE instr(body, $t) > 0 LIMIT 1" })
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$t", text);
                if (cmd.ExecuteScalar() != null) return true;
            }
            return false;
        }
    }

    public IReadOnlyList<StoredMessage> Newest(string roomKey, int perChannel) =>
        Query($"""
            SELECT {Columns} FROM (
                SELECT {Columns}, ROW_NUMBER() OVER (PARTITION BY chan ORDER BY ts DESC, key DESC) AS n
                FROM messages WHERE room = $r AND target IS NULL) WHERE n <= $n
            """, c => { c.Parameters.AddWithValue("$r", roomKey); c.Parameters.AddWithValue("$n", perChannel); });

    public IReadOnlyList<StoredMessage> Before(string roomKey, string? channel, long ts, string key, int count) =>
        Query($"""
            SELECT {Columns} FROM messages
            WHERE room = $r AND chan = $c AND target IS NULL AND (ts < $t OR (ts = $t AND key < $k))
            ORDER BY ts DESC, key DESC LIMIT $n
            """, c =>
        {
            c.Parameters.AddWithValue("$r", roomKey);
            c.Parameters.AddWithValue("$c", channel ?? "");
            c.Parameters.AddWithValue("$t", ts);
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$n", count);
        });

    private List<StoredMessage> ByList(string column, string roomKey, IReadOnlyCollection<string> values, string extra = "")
    {
        var all = new List<StoredMessage>();
        foreach (var part in values.Distinct().Chunk(Chunk))
            all.AddRange(Query($"SELECT {Columns} FROM messages WHERE room = $r AND {column} IN ({Marks(part.Length)}) {extra}", c =>
            {
                c.Parameters.AddWithValue("$r", roomKey);
                for (int i = 0; i < part.Length; i++) c.Parameters.AddWithValue("$" + i, part[i]);
            }));
        return all;
    }

    public IReadOnlyList<StoredMessage> Find(string roomKey, IReadOnlyCollection<string> keys) => ByList("key", roomKey, keys);

    public HashSet<string> Held(string roomKey, IReadOnlyCollection<string> keys)
    {
        var held = new HashSet<string>();
        lock (_gate)
            foreach (var part in keys.Distinct().Chunk(Chunk))
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = $"SELECT key FROM messages WHERE room = $r AND key IN ({Marks(part.Length)})";
                cmd.Parameters.AddWithValue("$r", roomKey);
                for (int i = 0; i < part.Length; i++) cmd.Parameters.AddWithValue("$" + i, part[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read()) held.Add(r.GetString(0));
            }
        return held;
    }

    public IReadOnlyList<StoredMessage> ReactionsTo(string roomKey, IReadOnlyCollection<string> targets) => ByList("target", roomKey, targets);

    public IReadOnlyList<StoredMessage> ByAuthors(string roomKey, IReadOnlyCollection<string> authors) => ByList("author", roomKey, authors);

    public IReadOnlyList<(string Key, StoredMessage Message)> Window(string roomKey, long sinceTs, int perChannel, int reactions)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT key, {Columns} FROM (
                    SELECT key, {Columns}, ROW_NUMBER() OVER (PARTITION BY chan ORDER BY ts DESC, key DESC) AS n
                    FROM messages WHERE room = $r AND target IS NULL AND ts >= $t) WHERE n <= $m
                UNION ALL
                SELECT key, {Columns} FROM (
                    SELECT key, {Columns} FROM messages WHERE room = $r AND target IS NOT NULL AND ts >= $t ORDER BY ts DESC, key DESC LIMIT $x)
                """;
            cmd.Parameters.AddWithValue("$r", roomKey);
            cmd.Parameters.AddWithValue("$t", sinceTs);
            cmd.Parameters.AddWithValue("$m", perChannel);
            cmd.Parameters.AddWithValue("$x", reactions);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, StoredMessage)>();
            while (r.Read()) list.Add((r.GetString(0), Read(r, 1)));
            return list;
        }
    }

    public string[] WindowKeys(string roomKey, long sinceTs, int perChannel, int reactions) =>
        Window(roomKey, sinceTs, perChannel, reactions).Select(w => w.Key).ToArray();

    /// <summary>
    /// Writes a copy of the whole database to <paramref name="path"/> that is consistent however busy the store is (SQLite's backup, not a copy of the file,
    /// which can catch a write half way). The copy has the same key as the database. An earlier file at the path is replaced.
    /// </summary>
    public void SnapshotTo(string path, byte[]? key)
    {
        if (File.Exists(path)) File.Delete(path);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        target.Open();
        if (key != null)
        {
            using var cmd = target.CreateCommand();
            cmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\"";
            cmd.ExecuteNonQuery();
        }
        // Writers wait while the copy is made.
        lock (_gate) _db.BackupDatabase(target);
    }

    // ---- direct messages ----

    private const string DirectColumns = "id, peer, name, mine, ts, body, pub, acct, cert, sig, st";

    private static DirectRecord ReadDirect(SqliteDataReader r, int at = 0) =>
        new(r.GetString(at), r.GetString(at + 1), r.GetString(at + 2), r.GetInt64(at + 3) != 0, r.GetInt64(at + 4), r.GetString(at + 5),
            r.GetString(at + 6), r.GetString(at + 7), r.GetString(at + 8), r.GetString(at + 9), r.GetInt64(at + 10));

    private List<DirectRecord> DirectQuery(string sql, Action<SqliteCommand> bind)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);
            using var r = cmd.ExecuteReader();
            var list = new List<DirectRecord>();
            while (r.Read()) list.Add(ReadDirect(r));
            return list;
        }
    }

    private static void BindDirect(SqliteCommand cmd, string key, DirectRecord m, string? target)
    {
        cmd.Parameters["$k"].Value = key;
        cmd.Parameters["$i"].Value = m.Id;
        cmd.Parameters["$p"].Value = m.Peer;
        cmd.Parameters["$n"].Value = m.Name;
        cmd.Parameters["$m"].Value = m.Mine ? 1 : 0;
        cmd.Parameters["$t"].Value = m.Ts;
        cmd.Parameters["$b"].Value = m.Text;
        cmd.Parameters["$u"].Value = m.Pub;
        cmd.Parameters["$a"].Value = m.Acct;
        cmd.Parameters["$c"].Value = m.Cert;
        cmd.Parameters["$s"].Value = m.Sig;
        cmd.Parameters["$x"].Value = m.St;
        cmd.Parameters["$g"].Value = (object?)target ?? DBNull.Value;
    }

    private SqliteCommand InsertDirect(SqliteTransaction? tx)
    {
        var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        // A message that was removed for good (kept under "d:<person>") is not kept again.
        cmd.CommandText = "INSERT OR IGNORE INTO direct(peer, key, id, name, mine, ts, body, pub, acct, cert, sig, st, target) " +
                          "SELECT $p, $k, $i, $n, $m, $t, $b, $u, $a, $c, $s, $x, $g WHERE NOT EXISTS (SELECT 1 FROM removed WHERE room = 'd:' || $p AND key = $k)";
        foreach (var n in new[] { "$p", "$k", "$i", "$n", "$m", "$t", "$b", "$u", "$a", "$c", "$s", "$x", "$g" }) cmd.Parameters.Add(new SqliteParameter(n, null));
        return cmd;
    }

    public bool AddDirect(string key, DirectRecord record, string? reactionTarget)
    {
        lock (_gate)
        {
            using var cmd = InsertDirect(null);
            BindDirect(cmd, key, record, reactionTarget);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int AddDirectMany(IEnumerable<(string Key, DirectRecord Record, string? Target)> records)
    {
        lock (_gate)
        {
            int added = 0;
            using var tx = _db.BeginTransaction();
            using var cmd = InsertDirect(tx);
            foreach (var (key, record, target) in records)
            {
                BindDirect(cmd, key, record, target);
                added += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return added;
        }
    }

    public IReadOnlyList<DirectRecord> DirectNewest(string peer, int count) =>
        DirectQuery($"SELECT {DirectColumns} FROM direct WHERE peer = $p AND target IS NULL ORDER BY ts DESC, key DESC LIMIT $n",
            c => { c.Parameters.AddWithValue("$p", peer); c.Parameters.AddWithValue("$n", count); });

    public IReadOnlyList<DirectRecord> DirectBefore(string peer, long ts, string key, int count) =>
        DirectQuery($"SELECT {DirectColumns} FROM direct WHERE peer = $p AND target IS NULL AND (ts < $t OR (ts = $t AND key < $k)) ORDER BY ts DESC, key DESC LIMIT $n", c =>
        {
            c.Parameters.AddWithValue("$p", peer);
            c.Parameters.AddWithValue("$t", ts);
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$n", count);
        });

    public DirectRecord? DirectFind(string peer, string key) =>
        DirectQuery($"SELECT {DirectColumns} FROM direct WHERE peer = $p AND key = $k", c => { c.Parameters.AddWithValue("$p", peer); c.Parameters.AddWithValue("$k", key); }).FirstOrDefault();

    public IReadOnlyList<DirectRecord> DirectReactionsTo(string peer, IReadOnlyCollection<string> targets)
    {
        var all = new List<DirectRecord>();
        foreach (var part in targets.Distinct().Chunk(Chunk))
            all.AddRange(DirectQuery($"SELECT {DirectColumns} FROM direct WHERE peer = $p AND target IN ({Marks(part.Length)})", c =>
            {
                c.Parameters.AddWithValue("$p", peer);
                for (int i = 0; i < part.Length; i++) c.Parameters.AddWithValue("$" + i, part[i]);
            }));
        return all;
    }

    public DirectRecord? DirectOwn(string peer, string id) =>
        DirectQuery($"SELECT {DirectColumns} FROM direct WHERE peer = $p AND id = $i AND mine = 1 LIMIT 1", c => { c.Parameters.AddWithValue("$p", peer); c.Parameters.AddWithValue("$i", id); }).FirstOrDefault();

    public bool DirectAny(string peer)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM direct WHERE peer = $p LIMIT 1";
            cmd.Parameters.AddWithValue("$p", peer);
            return cmd.ExecuteScalar() != null;
        }
    }

    public long DirectNewestIncoming(string peer)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(MAX(ts), 0) FROM direct WHERE peer = $p AND mine = 0";
            cmd.Parameters.AddWithValue("$p", peer);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public IReadOnlyList<string> DirectPeers()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT peer FROM direct";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public string DirectLastName(string peer)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT name FROM direct WHERE peer = $p AND mine = 0 AND name <> '' ORDER BY ts DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$p", peer);
            return cmd.ExecuteScalar() as string ?? "";
        }
    }

    public IReadOnlyList<(string Key, DirectRecord Record)> DirectWindow(long sinceTs, int perChat, int reactions, int cap)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT key, {DirectColumns} FROM (
                    SELECT key, {DirectColumns} FROM (
                        SELECT key, {DirectColumns}, ROW_NUMBER() OVER (PARTITION BY peer ORDER BY ts DESC, key DESC) AS n
                        FROM direct WHERE target IS NULL AND ts >= $t) WHERE n <= $m
                    UNION ALL
                    SELECT key, {DirectColumns} FROM (
                        SELECT key, {DirectColumns}, ROW_NUMBER() OVER (PARTITION BY peer ORDER BY ts DESC, key DESC) AS n
                        FROM direct WHERE target IS NOT NULL AND ts >= $t) WHERE n <= $x)
                ORDER BY ts DESC, key DESC LIMIT $c
                """;
            cmd.Parameters.AddWithValue("$t", sinceTs);
            cmd.Parameters.AddWithValue("$m", perChat);
            cmd.Parameters.AddWithValue("$x", reactions);
            cmd.Parameters.AddWithValue("$c", cap == int.MaxValue ? -1 : cap);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, DirectRecord)>();
            while (r.Read()) list.Add((r.GetString(0), ReadDirect(r, 1)));
            return list;
        }
    }

    private const string MonthOfTs = "strftime('%Y%m', ts / 1000, 'unixepoch')";

    public IReadOnlyList<string> Rooms()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT room FROM messages";
            using var r = cmd.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    private Dictionary<string, int> Counts(string table, string column, string who)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {MonthOfTs}, count(*) FROM {table} WHERE {column} = $w GROUP BY 1";
            cmd.Parameters.AddWithValue("$w", who);
            using var r = cmd.ExecuteReader();
            var counts = new Dictionary<string, int>();
            while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            return counts;
        }
    }

    private Dictionary<string, int> DayCountsOf(string table, string column, string who, string month)
    {
        var (from, to) = HistoryMonths.Range(month);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT strftime('%Y%m%d', ts / 1000, 'unixepoch'), count(*) FROM {table} WHERE {column} = $w AND ts >= $f AND ts < $t GROUP BY 1";
            cmd.Parameters.AddWithValue("$w", who);
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            var counts = new Dictionary<string, int>();
            while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            return counts;
        }
    }

    public IReadOnlyDictionary<string, int> DayCounts(string roomKey, string month) => DayCountsOf("messages", "room", roomKey, month);

    public IReadOnlyDictionary<string, int> DirectDayCounts(string peer, string month) => DayCountsOf("direct", "peer", peer, month);

    public IReadOnlyDictionary<string, int> MonthCounts(string roomKey) => Counts("messages", "room", roomKey);

    public IReadOnlyDictionary<string, int> DirectMonthCounts(string peer) => Counts("direct", "peer", peer);

    public IReadOnlyList<(string Key, StoredMessage Message, string? Target)> InMonth(string roomKey, string month)
    {
        var (from, to) = HistoryMonths.Range(month);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT key, target, {Columns} FROM messages WHERE room = $r AND ts >= $f AND ts < $t ORDER BY ts, key";
            cmd.Parameters.AddWithValue("$r", roomKey);
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, StoredMessage, string?)>();
            while (r.Read()) list.Add((r.GetString(0), Read(r, 2), r.IsDBNull(1) ? null : r.GetString(1)));
            return list;
        }
    }

    public IReadOnlyList<(string Key, DirectRecord Record, string? Target)> DirectInMonth(string peer, string month)
    {
        var (from, to) = HistoryMonths.Range(month);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT key, target, {DirectColumns} FROM direct WHERE peer = $p AND ts >= $f AND ts < $t ORDER BY ts, key";
            cmd.Parameters.AddWithValue("$p", peer);
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            using var r = cmd.ExecuteReader();
            var list = new List<(string, DirectRecord, string?)>();
            while (r.Read()) list.Add((r.GetString(0), ReadDirect(r, 2), r.IsDBNull(1) ? null : r.GetString(1)));
            return list;
        }
    }

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
