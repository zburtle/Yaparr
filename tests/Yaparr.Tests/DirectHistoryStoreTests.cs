using Yaparr.Chat;
using Yaparr.History.Sqlite;

namespace Yaparr.Tests;

// What a store has to do for direct messages, run against the one in memory and the SQLCipher one.
public class DirectHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yaparr-direct-" + Guid.NewGuid().ToString("N"));
    private static readonly string Alice = new('a', 64), Bob = new('b', 64);
    private static readonly byte[] Key = Enumerable.Range(5, 32).Select(i => (byte)i).ToArray();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    public static TheoryData<string> Kinds => new() { "memory", "sqlcipher" };

    private IDirectHistoryStore Make(string kind) => kind == "memory" ? new MemoryHistoryStore() : new SqliteHistoryStore(Path.Combine(_dir, "d.db"), Key);

    private static DirectRecord Msg(string peer, string id, long ts, bool mine = false, string text = "", string name = "") =>
        new(id, peer, name, mine, ts, text.Length > 0 ? text : $"text {id}");

    private static bool Add(IDirectHistoryStore s, DirectRecord r) => s.AddDirect(DirectHistory.Key(r), r, DirectHistory.ReactionTargetOf(r.Text));

    [Theory, MemberData(nameof(Kinds))]
    public void A_message_is_kept_once_per_conversation_and_found_by_key_and_id(string kind)
    {
        using var d = new Disposing(Make(kind));
        var s = d.Store;
        var m = Msg(Alice, "m1", 1000, mine: true);
        Assert.True(Add(s, m));
        Assert.False(Add(s, m));
        Assert.True(Add(s, Msg(Alice, "m1", 1000, mine: false)));    // theirs with the same id is another message
        Assert.True(Add(s, Msg(Bob, "m1", 1000, mine: true)));       // and so is the same id in another conversation

        Assert.Equal("text m1", s.DirectFind(Alice, DirectHistory.Key(m))!.Text);
        Assert.True(s.DirectOwn(Alice, "m1")!.Mine);
        Assert.Null(s.DirectOwn(Alice, "nope"));
        Assert.True(s.DirectAny(Alice));
        Assert.False(s.DirectAny(new string('c', 64)));
        Assert.Equal(new[] { Alice, Bob }, s.DirectPeers().OrderBy(x => x));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void The_newest_come_first_and_earlier_ones_page_back_without_gaps(string kind)
    {
        using var d = new Disposing(Make(kind));
        var s = d.Store;
        var all = Enumerable.Range(0, 12).Select(i => Msg(Alice, $"m{i:00}", 1000 + i / 2)).ToList();   // pairs share a time
        foreach (var r in all) Add(s, r);
        Add(s, Msg(Bob, "other", 5000));

        var newest = s.DirectNewest(Alice, 5);
        Assert.Equal(5, newest.Count);
        var order = s.DirectNewest(Alice, 100).OrderByDescending(r => r.Ts).ThenByDescending(DirectHistory.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(order.Take(5).Select(r => r.Id), newest.Select(r => r.Id));

        var seen = newest.Select(r => r.Id).ToList();
        var cursor = order[4];
        while (true)
        {
            var next = s.DirectBefore(Alice, cursor.Ts, DirectHistory.Key(cursor), 4);
            if (next.Count == 0) break;
            seen.AddRange(next.Select(r => r.Id));
            cursor = next[^1];
        }
        Assert.Equal(order.Select(r => r.Id), seen);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void Reactions_are_found_by_the_message_they_are_on_and_are_not_messages(string kind)
    {
        using var d = new Disposing(Make(kind));
        var s = d.Store;
        var target = new string('d', 32);
        Add(s, Msg(Alice, "m1", 1000));
        Add(s, Msg(Alice, "r1", 1001, text: MessageEnvelope.WrapReaction(target, "👍", true)));
        Add(s, Msg(Alice, "r2", 1002, text: MessageEnvelope.WrapReaction(new string('e', 32), "🎉", true)));

        Assert.Equal("r1", Assert.Single(s.DirectReactionsTo(Alice, new[] { target })).Id);
        Assert.Equal("m1", Assert.Single(s.DirectNewest(Alice, 10)).Id);
    }

    [Theory, MemberData(nameof(Kinds))]
    public void The_newest_incoming_time_and_the_last_name_come_from_what_they_sent(string kind)
    {
        using var d = new Disposing(Make(kind));
        var s = d.Store;
        Add(s, Msg(Alice, "a", 1000, name: "Al"));
        Add(s, Msg(Alice, "b", 3000, mine: true, name: "me"));
        Add(s, Msg(Alice, "c", 2000, name: "Alice"));

        Assert.Equal(2000, s.DirectNewestIncoming(Alice));
        Assert.Equal("Alice", s.DirectLastName(Alice));
        Assert.Equal(0, s.DirectNewestIncoming(Bob));
        Assert.Equal("", s.DirectLastName(Bob));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void The_window_for_other_devices_is_recent_limited_per_conversation_and_capped_overall(string kind)
    {
        using var d = new Disposing(Make(kind));
        var s = d.Store;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 6; i++) Add(s, Msg(Alice, $"a{i}", now - 1000 + i));
        for (int i = 0; i < 2; i++) Add(s, Msg(Bob, $"b{i}", now - 2000 + i));
        Add(s, Msg(Alice, "ancient", now - 100_000_000));

        var window = s.DirectWindow(now - 50_000, 3, 12, int.MaxValue).Select(w => w.Record.Id).ToList();
        Assert.Equal(new[] { "a3", "a4", "a5" }, window.Where(i => i.StartsWith('a')).OrderBy(x => x));
        Assert.Equal(2, window.Count(i => i.StartsWith('b')));
        Assert.DoesNotContain("ancient", window);
        Assert.Equal(4, s.DirectWindow(now - 50_000, 3, 12, 4).Count);
        // Newest first.
        var times = s.DirectWindow(now - 50_000, 3, 12, 100).Select(w => w.Record.Ts).ToList();
        Assert.Equal(times.OrderByDescending(t => t), times);
        // Being old keeps nothing out of the conversation.
        Assert.Equal(2, s.DirectNewest(Alice, 100).Count(r => r.Id is "ancient" or "a0"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public void A_message_removed_for_good_is_gone_and_is_not_kept_again(string kind)
    {
        using var d = new Disposing(Make(kind));
        var s = d.Store;
        var gone = Msg(Alice, "m1", 1000, mine: true);
        var kept = Msg(Alice, "m2", 2000, mine: true);
        Add(s, gone); Add(s, kept);

        var chat = (IChatHistoryStore)s;
        Assert.Equal(1, chat.ApplyRemoved(DirectRemovals.Prefix + Alice, new[] { DirectHistory.Key(gone) }));
        Assert.Equal(0, chat.ApplyRemoved(DirectRemovals.Prefix + Alice, new[] { DirectHistory.Key(gone) }));
        Assert.Null(s.DirectFind(Alice, DirectHistory.Key(gone)));
        Assert.Equal(new[] { "m2" }, s.DirectNewest(Alice, 10).Select(r => r.Id));
        Assert.False(Add(s, gone));                                   // an old copy brought back by another device
        Assert.Equal(0, s.AddDirectMany(new[] { (DirectHistory.Key(gone), gone, (string?)null) }));
        Assert.Equal(new[] { DirectHistory.Key(gone) }, chat.RemovedKeys(DirectRemovals.Prefix + Alice));
        Assert.Contains(DirectRemovals.Prefix + Alice, chat.RoomsWithRemoved());
    }

    [Theory, MemberData(nameof(Kinds))]
    public void What_was_removed_travels_with_the_kept_history_and_stays_removed_in_any_order(string kind)
    {
        using var d = new Disposing(Make(kind));
        var s = d.Store;
        var chat = (IChatHistoryStore)s;
        var month = HistoryMonths.Of(1_700_000_000_000);
        var gone = Msg(Alice, "m1", 1_700_000_000_000, mine: true);
        var kept = Msg(Alice, "m2", 1_700_000_001_000, mine: true);
        Add(s, gone); Add(s, kept);
        var monthPiece = HistoryPieces.Build(chat, HistoryPieces.DirectName(Alice, month))!;
        chat.ApplyRemoved(DirectRemovals.Prefix + Alice, new[] { DirectHistory.Key(gone) });
        var removedPiece = HistoryPieces.Build(chat, HistoryPieces.RemovedName(DirectRemovals.Prefix + Alice))!;
        Assert.Equal((true, Alice), HistoryPieces.WhoOf(HistoryPieces.RemovedName(DirectRemovals.Prefix + Alice)));

        // Another device that still holds both takes the removal, and the old month cannot bring the message back.
        var other = Path.Combine(_dir, "other.db");
        using var o = new Disposing(kind == "memory" ? new MemoryHistoryStore() : new SqliteHistoryStore(other, Key));
        var otherChat = (IChatHistoryStore)o.Store;
        Assert.True(HistoryPieces.Take(otherChat, HistoryPieces.DirectName(Alice, month), monthPiece) >= 0);
        Assert.True(HistoryPieces.Take(otherChat, HistoryPieces.RemovedName(DirectRemovals.Prefix + Alice), removedPiece) >= 0);
        Assert.True(HistoryPieces.Take(otherChat, HistoryPieces.DirectName(Alice, month), monthPiece) >= 0);
        Assert.Equal(new[] { "m2" }, o.Store.DirectNewest(Alice, 10).Select(r => r.Id));
    }

    private sealed class Disposing(IDirectHistoryStore store) : IDisposable
    {
        public IDirectHistoryStore Store { get; } = store;
        public void Dispose() => (Store as IDisposable)?.Dispose();
    }
}
