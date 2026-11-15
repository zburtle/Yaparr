using System.Text;
using Yaparr.Chat;
using Yaparr.History.Sqlite;
using Yaparr.Identity;

namespace Yaparr.Tests;

// What a history store has to do, run against the one in memory and the SQLCipher one (with and without a key).
public class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-history-" + Guid.NewGuid().ToString("N"));
    private readonly PeerIdentity _a = PeerIdentity.Generate();
    private readonly PeerIdentity _b = PeerIdentity.Generate();
    private const string Room = "room";
    private const string Other = "1111111111111111", Second = "2222222222222222";

    public void Dispose()
    {
        _a.Dispose(); _b.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    public static TheoryData<string> Kinds => new() { "memory", "sqlite", "sqlcipher" };

    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private IChatHistoryStore Make(string kind) => kind switch
    {
        "memory" => new MemoryHistoryStore(),
        "sqlite" => new SqliteHistoryStore(Path.Combine(_dir, "plain.db")),
        _ => new SqliteHistoryStore(Path.Combine(_dir, "sealed.db"), Key),
    };

    private static bool Add(IChatHistoryStore store, StoredMessage m)
    {
        string? target = MessageEnvelope.TryReadReaction(m.Text, out var reaction) ? reaction.Target : null;
        return store.Add(Room, SignedMessage.Key(m), m, target);
    }

    private StoredMessage Say(string text, long ts, string? chan = null, PeerIdentity? by = null) => SignedMessage.Create(by ?? _a, Room, "x", text, ts, chan);

    [Theory, MemberData(nameof(Kinds))]
    public void A_message_is_kept_once_and_can_be_found_and_removed(string kind)
    {
        using var store = new Disposing(Make(kind));
        var m = Say("hello", 1000);
        Assert.True(Add(store.Store, m));
        Assert.False(Add(store.Store, m));
        var key = SignedMessage.Key(m);
        Assert.Equal("hello", Assert.Single(store.Store.Find(Room, new[] { key })).Text);
        Assert.Equal(new[] { key }, store.Store.Held(Room, new[] { key, "nope" }));
        store.Store.Remove(Room, new[] { key });
        Assert.Empty(store.Store.Find(Room, new[] { key }));
        Assert.Empty(store.Store.Held(Room, new[] { key }));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void The_newest_of_each_channel_are_returned_and_reactions_are_not_messages(string kind)
    {
        using var s = new Disposing(Make(kind));
        var store = s.Store;
        for (int i = 0; i < 7; i++) Add(store, Say($"busy {i}", 1000 + i, Other));
        for (int i = 0; i < 2; i++) Add(store, Say($"quiet {i}", 500 + i, Second));
        Add(store, Say("first channel", 2000));
        var target = Say("target", 3000, Other);
        Add(store, target);
        Add(store, Say(MessageEnvelope.WrapReaction(SignedMessage.Key(target), "👍", true), 3001, Other));

        var newest = store.Newest(Room, 3).Select(m => m.Text).ToList();
        Assert.Equal(new[] { "busy 5", "busy 6", "target" }, newest.Where(t => t.StartsWith("busy") || t == "target").OrderBy(x => x));
        Assert.Contains("quiet 0", newest);
        Assert.Contains("quiet 1", newest);
        Assert.Contains("first channel", newest);
        Assert.DoesNotContain(newest, t => MessageEnvelope.IsReactionText(t));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Earlier_messages_come_newest_first_from_the_one_given_and_in_its_own_channel(string kind)
    {
        using var s = new Disposing(Make(kind));
        var store = s.Store;
        var ms = Enumerable.Range(0, 10).Select(i => Say($"m{i}", 1000 + i, Other)).ToList();
        foreach (var m in ms) Add(store, m);
        Add(store, Say("elsewhere", 1004, Second));

        Assert.Equal(new[] { "m6", "m5", "m4" }, store.Before(Room, Other, ms[7].Ts, SignedMessage.Key(ms[7]), 3).Select(m => m.Text));
        Assert.Equal(new[] { "m1", "m0" }, store.Before(Room, Other, ms[2].Ts, SignedMessage.Key(ms[2]), 5).Select(m => m.Text));
        Assert.Empty(store.Before(Room, Other, ms[0].Ts, SignedMessage.Key(ms[0]), 5));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Messages_with_the_same_time_are_paged_without_gaps_or_repeats(string kind)
    {
        using var s = new Disposing(Make(kind));
        var store = s.Store;
        foreach (var m in Enumerable.Range(0, 9).Select(i => Say($"same {i}", 5000))) Add(store, m);
        var order = store.Newest(Room, 100).OrderByDescending(m => m.Ts).ThenByDescending(SignedMessage.Key, StringComparer.Ordinal).ToList();
        var seen = new List<string> { SignedMessage.Key(order[0]) };
        var cursor = order[0];
        while (true)
        {
            var next = store.Before(Room, null, cursor.Ts, SignedMessage.Key(cursor), 2);
            if (next.Count == 0) break;
            seen.AddRange(next.Select(SignedMessage.Key));
            cursor = next[^1];
        }
        Assert.Equal(order.Select(SignedMessage.Key), seen);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Reactions_are_found_by_the_message_they_are_on_and_messages_by_device(string kind)
    {
        using var s = new Disposing(Make(kind));
        var store = s.Store;
        var target = Say("target", 1000);
        var elsewhere = Say("other target", 1001);
        Add(store, target); Add(store, elsewhere);
        Add(store, Say(MessageEnvelope.WrapReaction(SignedMessage.Key(target), "👍", true), 1002, by: _b));
        Add(store, Say(MessageEnvelope.WrapReaction(SignedMessage.Key(elsewhere), "🎉", true), 1003, by: _b));

        var onTarget = store.ReactionsTo(Room, new[] { SignedMessage.Key(target) });
        Assert.Equal(_b.Id.Value, Assert.Single(onTarget).From);
        Assert.Equal(2, store.ByAuthors(Room, new[] { _b.Id.Value }).Count);
        Assert.Equal(2, store.ByAuthors(Room, new[] { _a.Id.Value }).Count);
        Assert.Empty(store.ReactionsTo(Room, Array.Empty<string>()));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void The_shared_window_is_recent_and_limited_per_channel_and_rooms_do_not_mix(string kind)
    {
        using var s = new Disposing(Make(kind));
        var store = s.Store;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 6; i++) Add(store, Say($"busy {i}", now - 10_000 + i, Other));
        for (int i = 0; i < 2; i++) Add(store, Say($"quiet {i}", now - 20_000 + i, Second));
        var ancient = Say("ancient", now - 100_000_000);
        Add(store, ancient);
        var theirs = Say("another room", now, by: _b);
        store.Add("elsewhere", SignedMessage.Key(theirs), theirs, null);

        var window = store.Window(Room, now - 50_000, 3, 12).Select(w => w.Message.Text).ToList();
        Assert.Equal(new[] { "busy 3", "busy 4", "busy 5" }, window.Where(t => t.StartsWith("busy")).OrderBy(x => x));
        Assert.Equal(2, window.Count(t => t.StartsWith("quiet")));
        Assert.DoesNotContain("ancient", window);
        Assert.DoesNotContain("another room", window);
        Assert.Equal(store.Window(Room, now - 50_000, 3, 12).Select(w => w.Key).OrderBy(x => x), store.WindowKeys(Room, now - 50_000, 3, 12).OrderBy(x => x));
        // Being old keeps nothing out of the history: the message is still there for a quote to find.
        Assert.Single(store.Find(Room, new[] { SignedMessage.Key(ancient) }));
    }

    [Fact]
    public void An_encrypted_database_shows_nothing_in_the_file_and_only_opens_with_its_key()
    {
        var path = Path.Combine(_dir, "secret.db");
        var m = Say("the meeting is at the old mill", 1000);
        using (var store = new SqliteHistoryStore(path, Key)) Add(store, m);

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual("SQLite format 3", Encoding.ASCII.GetString(bytes, 0, 15));
        Assert.DoesNotContain("old mill", Encoding.UTF8.GetString(bytes));

        using (var again = new SqliteHistoryStore(path, Key)) Assert.Single(again.Find(Room, new[] { SignedMessage.Key(m) }));
        var wrong = Key.ToArray(); wrong[0] ^= 1;
        Assert.ThrowsAny<Exception>(() => new SqliteHistoryStore(path, wrong));
        Assert.ThrowsAny<Exception>(() => new SqliteHistoryStore(path, null));
    }

    [Fact]
    public void A_database_that_cannot_be_opened_is_kept_aside_and_a_new_one_is_made()
    {
        var path = Path.Combine(_dir, "lost.db");
        var m = Say("kept aside", 1000);
        using (var store = new SqliteHistoryStore(path, Key)) Add(store, m);

        var other = Enumerable.Range(40, 32).Select(i => (byte)i).ToArray();
        using (var fresh = SqliteHistoryStore.Open(path, other))
        {
            Assert.Empty(fresh.Find(Room, new[] { SignedMessage.Key(m) }));
            Add(fresh, Say("new", 2000));
        }
        Assert.True(File.Exists(path + ".unreadable"));
        using var old = new SqliteHistoryStore(path + ".unreadable", Key);
        Assert.Single(old.Find(Room, new[] { SignedMessage.Key(m) }));
    }

    // Closes a database store at the end of a test (the one in memory has nothing to close).
    private sealed class Disposing(IChatHistoryStore store) : IDisposable
    {
        public IChatHistoryStore Store { get; } = store;
        public void Dispose() => (Store as IDisposable)?.Dispose();
    }
}
