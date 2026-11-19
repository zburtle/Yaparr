using System.Security.Cryptography;
using System.Text;
using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Identity;

namespace Yaparr.Tests;

/// <summary>A server's kept store as the real one holds it: pieces by the hash of their bytes, a quota, and what a call was, for the tests to look at.</summary>
internal sealed class FakeKeepServer : IKeepTransport
{
    public readonly Dictionary<string, byte[]> Pieces = new();
    public long Quota { get; set; } = long.MaxValue;
    public int Puts { get; private set; }
    public int Deletes { get; private set; }

    public Task<KeepPutResult> PutAsync(string id, byte[] locked, CancellationToken cancel = default)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(locked)) != id) throw new InvalidOperationException("the id is not the hash");
        if (Pieces.Where(p => p.Key != id).Sum(p => p.Value.Length) + locked.Length > Quota) return Task.FromResult(KeepPutResult.Full);
        Puts++;
        Pieces[id] = locked;
        return Task.FromResult(KeepPutResult.Stored);
    }

    public Task<byte[]?> GetAsync(string id, CancellationToken cancel = default) => Task.FromResult(Pieces.GetValueOrDefault(id));

    public Task DeleteAsync(string id, CancellationToken cancel = default)
    {
        Deletes++;
        Pieces.Remove(id);
        return Task.CompletedTask;
    }
}

public sealed class HistoryPieceTests : IDisposable
{
    private const string Code = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB";
    private static readonly string Room = new('a', 64), Peer = new('b', 64);
    private readonly PeerIdentity _alice = PeerIdentity.Generate();
    private readonly PeerIdentity _stranger = PeerIdentity.Generate();
    private readonly FakeSyncServer _records = new();
    private readonly FakeKeepServer _keep = new();
    private long _now = 1_800_000_000_000;

    public void Dispose() { _alice.Dispose(); _stranger.Dispose(); }

    private static long Days(int ago) => DateTimeOffset.UtcNow.AddDays(-ago).ToUnixTimeMilliseconds();

    private sealed class Device
    {
        public required MemoryHistoryStore Store;
        public required SyncEngine Engine;
        public required PieceSync Pieces;
    }

    private Device NewDevice(string id, string code = Code)
    {
        var store = new MemoryHistoryStore();
        var sealer = SyncSealer.FromRecoveryCode(code);
        var engine = new SyncEngine(new MemorySyncState(), _records, sealer, new SyncClock(id, () => DateTimeOffset.FromUnixTimeMilliseconds(_now)), id);
        return new Device { Store = store, Engine = engine, Pieces = new PieceSync(engine, _keep, sealer, new HistoryPieceSource(store)) };
    }

    private static async Task<PieceReport> Run(Device d)
    {
        await d.Engine.SyncAsync();
        return await d.Pieces.RunAsync();
    }

    private StoredMessage Say(Device d, string text, int daysAgo, string room = "")
    {
        var m = SignedMessage.Create(_alice, room.Length > 0 ? room : Room, "Alice", text, Days(daysAgo));
        d.Store.Add(room.Length > 0 ? room : Room, SignedMessage.Key(m), m, null);
        return m;
    }

    private static int Count(Device d, string room = "") => d.Store.MonthCounts(room.Length > 0 ? room : Room).Values.Sum();

    [Fact]
    public async Task A_months_history_is_kept_locked_and_a_new_device_gets_it_back()
    {
        var phone = NewDevice("phone");
        Say(phone, "the secret plan", 40);
        Say(phone, "another message in the same month", 40);
        Say(phone, "a message from this month", 1);
        var report = await Run(phone);
        Assert.True(report.Uploaded >= 1);
        Assert.All(_keep.Pieces.Values, bytes => Assert.DoesNotContain("secret plan", Encoding.UTF8.GetString(bytes)));

        var laptop = NewDevice("laptop");
        var got = await Run(laptop);
        Assert.True(got.Downloaded >= 1);
        Assert.Equal(3, Count(laptop));
        Assert.Equal(0, (await Run(laptop)).Uploaded);                 // what it took in is what the account holds: nothing to write back
    }

    [Fact]
    public async Task Direct_messages_are_kept_the_same_way()
    {
        var phone = NewDevice("phone");
        var record = new DirectRecord("m1", Peer, "Bob", Mine: false, Ts: Days(3), Text: "hello there");
        phone.Store.AddDirect(DirectHistory.Key(record), record, null);
        await Run(phone);

        var laptop = NewDevice("laptop");
        await Run(laptop);
        Assert.Equal("hello there", laptop.Store.DirectFind(Peer, DirectHistory.Key(record))?.Text);
    }

    [Fact]
    public async Task A_message_added_later_replaces_the_piece_and_the_old_one_is_removed()
    {
        var phone = NewDevice("phone");
        var laptop = NewDevice("laptop");
        Say(phone, "first", 2);
        await Run(phone);
        var before = _keep.Pieces.Keys.ToHashSet();

        Say(phone, "second", 2);
        var report = await Run(phone);
        Assert.Equal(1, report.Uploaded);
        Assert.Equal(1, report.Removed);
        Assert.Single(_keep.Pieces);
        Assert.DoesNotContain(before.Single(), _keep.Pieces.Keys);

        await Run(laptop);
        Assert.Equal(2, Count(laptop));
    }

    [Fact]
    public async Task Two_devices_that_wrote_the_same_month_apart_end_up_with_all_of_it()
    {
        var phone = NewDevice("phone");
        var laptop = NewDevice("laptop");
        Say(phone, "from the phone", 2);
        Say(laptop, "from the laptop", 2);

        await Run(phone);
        await Run(laptop);     // takes the phone's in, and writes the whole month
        await Run(phone);      // takes that in
        await Run(laptop);
        Assert.Equal(2, Count(phone));
        Assert.Equal(2, Count(laptop));
        Assert.Equal(0, (await Run(phone)).Uploaded);
        Assert.Equal(0, (await Run(laptop)).Uploaded);
    }

    [Fact]
    public async Task What_is_taken_in_has_to_be_signed_for_that_room_and_belong_to_that_month()
    {
        var phone = NewDevice("phone");
        Say(phone, "genuine", 2);
        await Run(phone);

        // A piece for another room built from the same rows: the signatures are for the first room, so none of it is kept.
        var content = HistoryPieces.Build(phone.Store, HistoryPieces.RoomName(Room, HistoryMonths.Of(Days(2))))!;
        var other = new MemoryHistoryStore();
        Assert.Equal(0, HistoryPieces.Take(other, HistoryPieces.RoomName(new string('c', 64), HistoryMonths.Of(Days(2))), content));
        Assert.Empty(other.Rooms());
        // The wrong month: nothing is kept either.
        var wrongMonth = HistoryMonths.Of(Days(200));
        Assert.Equal(0, HistoryPieces.Take(other, HistoryPieces.RoomName(Room, wrongMonth), content));
        // Not a piece at all.
        Assert.Equal(-1, HistoryPieces.Take(other, HistoryPieces.RoomName(Room, wrongMonth), new byte[] { 1, 2, 3 }));
        Assert.Equal(1, HistoryPieces.Take(other, HistoryPieces.RoomName(Room, HistoryMonths.Of(Days(2))), content));
    }

    [Fact]
    public async Task A_piece_made_with_another_accounts_key_is_not_believed()
    {
        var mine = NewDevice("phone");
        var theirs = NewDevice("tablet", code: "AAAA-BBBB-CCCC-DDDD-EEEE-FFFF");
        Say(theirs, "not for you", 2);
        // They write to the same server (a shared fake): the records are sealed with their key, so this account's device cannot open them.
        await Run(theirs);
        var report = await Run(mine);
        Assert.Equal(0, report.Downloaded);
        Assert.Equal(0, Count(mine));
    }

    [Fact]
    public async Task A_full_account_stops_writing_and_says_so()
    {
        var phone = NewDevice("phone");
        for (int month = 0; month < 3; month++) Say(phone, new string('x', 50), 40 * (month + 1));
        _keep.Quota = 1;
        var report = await Run(phone);
        Assert.True(report.Full);
        Assert.Equal(0, report.Uploaded);
        Assert.Empty(_keep.Pieces);
    }

    [Fact]
    public void Months_are_utc_and_sort_in_time_order()
    {
        Assert.Equal("202611", HistoryMonths.Of(new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        Assert.Equal("202612", HistoryMonths.Of(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        var (from, to) = HistoryMonths.Range("202612");
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), to);
        Assert.True(from < to);
        Assert.False(HistoryMonths.IsMonth("202613"));
        Assert.False(HistoryMonths.IsMonth("2026-1"));
    }
}

public sealed class SqliteHistoryPieceTests : IDisposable
{
    private static readonly string Room = new('d', 64), Peer = new('e', 64);
    private readonly PeerIdentity _who = PeerIdentity.Generate();

    public void Dispose() => _who.Dispose();

    [Fact]
    public void The_database_counts_and_lists_a_months_rows_like_the_memory_store_and_pieces_move_between_them()
    {
        using var db = new Yaparr.History.Sqlite.SqliteHistoryStore(null);
        var memory = new MemoryHistoryStore();
        long ago(int d) => DateTimeOffset.UtcNow.AddDays(-d).ToUnixTimeMilliseconds();
        foreach (var store in new IChatHistoryStore[] { db, memory })
        {
            foreach (var (text, days) in new[] { ("one", 2), ("two", 2), ("old", 90) })
            {
                var m = SignedMessage.Create(_who, Room, "Zed", text, ago(days));
                store.Add(Room, SignedMessage.Key(m), m, null);
            }
            var d = new DirectRecord("x1", Peer, "Bob", Mine: true, Ts: ago(5), Text: "hi");
            ((IDirectHistoryStore)store).AddDirect(DirectHistory.Key(d), d, null);
        }

        Assert.Equal(memory.MonthCounts(Room).OrderBy(p => p.Key), db.MonthCounts(Room).OrderBy(p => p.Key));
        Assert.Equal(memory.DirectMonthCounts(Peer).OrderBy(p => p.Key), db.DirectMonthCounts(Peer).OrderBy(p => p.Key));
        Assert.Equal(new[] { Room }, db.Rooms());
        var month = HistoryMonths.Of(ago(2));
        Assert.Equal(memory.InMonth(Room, month).Count, db.InMonth(Room, month).Count);   // the ids are random, so the keys differ

        var piece = HistoryPieces.Build(db, HistoryPieces.RoomName(Room, month))!;
        using var other = new Yaparr.History.Sqlite.SqliteHistoryStore(null);
        Assert.Equal(db.InMonth(Room, month).Count, HistoryPieces.Take(other, HistoryPieces.RoomName(Room, month), piece));
        Assert.Equal(0, HistoryPieces.Take(other, HistoryPieces.RoomName(Room, month), piece));   // taking again changes nothing
        var dpiece = HistoryPieces.Build(db, HistoryPieces.DirectName(Peer, HistoryMonths.Of(ago(5))))!;
        Assert.Equal(1, HistoryPieces.Take(other, HistoryPieces.DirectName(Peer, HistoryMonths.Of(ago(5))), dpiece));
    }
}
