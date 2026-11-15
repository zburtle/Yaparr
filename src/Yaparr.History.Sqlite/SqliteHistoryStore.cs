using System.Text;
using Microsoft.Data.Sqlite;
using Yaparr.Chat;

namespace Yaparr.History.Sqlite;

/// <summary>
/// The messages of every room, in one SQLite database that SQLCipher encrypts as a whole (the key comes from <see cref="Yaparr.Identity.HistoryCipher.DatabaseKey"/>),
/// so the file shows nothing of who said what or when. Everything is kept; nothing is read whole (see <see cref="IChatHistoryStore"/>).
/// </summary>
public sealed class SqliteHistoryStore : IChatHistoryStore, IDisposable
{
    private const int Version = 1, Chunk = 400;

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
            CREATE TABLE IF NOT EXISTS messages(
                room TEXT NOT NULL, key TEXT NOT NULL, author TEXT NOT NULL, pub TEXT NOT NULL, name TEXT NOT NULL, id TEXT NOT NULL,
                ts INTEGER NOT NULL, body TEXT NOT NULL, sig TEXT NOT NULL, chan TEXT NOT NULL DEFAULT '', target TEXT,
                PRIMARY KEY(room, key)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS messages_by_time ON messages(room, chan, ts, key) WHERE target IS NULL;
            CREATE INDEX IF NOT EXISTS messages_by_target ON messages(room, target) WHERE target IS NOT NULL;
            CREATE INDEX IF NOT EXISTS messages_by_author ON messages(room, author);
            CREATE INDEX IF NOT EXISTS messages_by_ts ON messages(room, ts);
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

    public bool Add(string roomKey, string key, StoredMessage m, string? reactionTarget)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO messages(room, key, author, pub, name, id, ts, body, sig, chan, target) VALUES($r, $k, $a, $p, $n, $i, $t, $b, $s, $c, $g)";
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
            cmd.CommandText = "INSERT OR IGNORE INTO messages(room, key, author, pub, name, id, ts, body, sig, chan, target) VALUES($r, $k, $a, $p, $n, $i, $t, $b, $s, $c, $g)";
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

    public void Dispose()
    {
        lock (_gate) _db.Dispose();
    }
}
