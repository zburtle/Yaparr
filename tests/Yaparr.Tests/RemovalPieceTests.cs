using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public sealed class RemovedMessageStoreTests : IDisposable
{
    private static readonly string Room = new('a', 64);
    private readonly PeerIdentity _who = PeerIdentity.Generate();

    public void Dispose() => _who.Dispose();

    private IEnumerable<IChatHistoryStore> Stores()
    {
        yield return new MemoryHistoryStore();
        yield return new Yaparr.History.Sqlite.SqliteHistoryStore(null);
    }

    [Fact]
    public void A_removed_message_is_never_kept_again_however_it_comes_back()
    {
        foreach (var store in Stores())
        {
            var keep = SignedMessage.Create(_who, Room, "Zed", "stays", DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
            var gone = SignedMessage.Create(_who, Room, "Zed", "goes", DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
            Assert.True(store.Add(Room, SignedMessage.Key(keep), keep, null));
            Assert.True(store.Add(Room, SignedMessage.Key(gone), gone, null));

            store.ApplyRemoved(Room, new[] { SignedMessage.Key(gone) });
            Assert.Equal(new[] { SignedMessage.Key(gone) }, store.RemovedKeys(Room));
            Assert.Equal(new[] { Room }, store.RoomsWithRemoved());
            Assert.Single(store.Newest(Room, 10));

            Assert.False(store.Add(Room, SignedMessage.Key(gone), gone, null));                                   // one at a time
            Assert.Equal(0, store.AddMany(Room, new[] { (SignedMessage.Key(gone), gone, (string?)null) }));        // many at once
            Assert.Single(store.Newest(Room, 10));
            Assert.Equal(0, store.ApplyRemoved(Room, new[] { SignedMessage.Key(gone) }));                          // already remembered
            Assert.Equal(1, store.ApplyRemoved(Room, new[] { SignedMessage.Key(keep) }));                          // removing what is held, and remembering it
            Assert.Empty(store.Newest(Room, 10));
            (store as IDisposable)?.Dispose();
        }
    }
}

public sealed class RemovedPieceTests : IDisposable
{
    private const string Code = "K7QD-M3XF-9PLR-2WTE-HV8N-C4YB";
    private static readonly string Room = new('b', 64);
    private readonly PeerIdentity _alice = PeerIdentity.Generate();
    private readonly FakeSyncServer _records = new();
    private readonly FakeKeepServer _keep = new();
    private long _now = 1_800_000_000_000;

    public void Dispose() => _alice.Dispose();

    private sealed class Device
    {
        public required MemoryHistoryStore Store;
        public required SyncEngine Engine;
        public PieceSync Pieces = null!;
        public readonly List<(string Name, int Changed)> Taken = new();
    }

    private Device NewDevice(string id)
    {
        var store = new MemoryHistoryStore();
        var sealer = SyncSealer.FromRecoveryCode(Code);
        var engine = new SyncEngine(new MemorySyncState(), _records, sealer, new SyncClock(id, () => DateTimeOffset.FromUnixTimeMilliseconds(_now)), id);
        var device = new Device { Store = store, Engine = engine };
        device.Pieces = new PieceSync(engine, _keep, sealer, new HistoryPieceSource(store, (name, changed) => device.Taken.Add((name, changed))));
        return device;
    }

    private static async Task Run(Device d)
    {
        await d.Engine.SyncAsync();
        await d.Pieces.RunAsync();
    }

    private StoredMessage Say(Device d, string text)
    {
        var m = SignedMessage.Create(_alice, Room, "Alice", text, DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds());
        d.Store.Add(Room, SignedMessage.Key(m), m, null);
        return m;
    }

    private static int Count(Device d) => d.Store.MonthCounts(Room).Values.Sum();

    [Fact]
    public async Task A_message_removed_on_one_device_stays_removed_on_another_that_still_has_it_and_on_one_that_is_given_an_old_month()
    {
        var phone = NewDevice("phone");
        var laptop = NewDevice("laptop");
        var tablet = NewDevice("tablet");
        var keep = Say(phone, "keep me");
        var gone = Say(phone, "remove me");
        await Run(phone);
        await Run(laptop);                  // the laptop has both
        Assert.Equal(2, Count(laptop));

        phone.Store.ApplyRemoved(Room, new[] { SignedMessage.Key(gone) });
        await Run(phone);                   // the removal piece, and the month rewritten without it
        await Run(laptop);                  // takes the removal in: the message goes from the laptop too
        Assert.Equal(1, Count(laptop));
        Assert.Equal(SignedMessage.Key(keep), Assert.Single(laptop.Store.Newest(Room, 10)) is { } m ? SignedMessage.Key(m) : "");

        // A device that was never told anything gets the month and the removal in either order and ends up with the same.
        await Run(tablet);
        Assert.Equal(1, Count(tablet));
        Assert.DoesNotContain(tablet.Store.Newest(Room, 10), x => x.Text == "remove me");
        Assert.Contains(tablet.Taken, t => t.Name.StartsWith("x-", StringComparison.Ordinal));

        // The old copy of the month, written before the removal, is not able to bring it back.
        var stale = HistoryPieces.Build(laptop.Store, HistoryPieces.RoomName(Room, HistoryMonths.Of(DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds())))!;
        var fresh = new MemoryHistoryStore();
        fresh.Add(Room, SignedMessage.Key(gone), gone, null);
        fresh.Add(Room, SignedMessage.Key(keep), keep, null);
        var oldMonth = HistoryPieces.Build(fresh, HistoryPieces.RoomName(Room, HistoryMonths.Of(gone.Ts)))!;
        Assert.True(HistoryPieces.Take(tablet.Store, HistoryPieces.RoomName(Room, HistoryMonths.Of(gone.Ts)), oldMonth) >= 0);
        Assert.Equal(1, Count(tablet));
        Assert.NotNull(stale);

        await Run(phone);
        await Run(laptop);
        await Run(tablet);
        Assert.Equal(1, Count(phone));
        Assert.Equal(1, Count(laptop));
        Assert.Equal(1, Count(tablet));
        Assert.Equal(0, (await RunReport(tablet)).Uploaded);          // and nothing is left to write
    }

    private static async Task<PieceReport> RunReport(Device d)
    {
        await d.Engine.SyncAsync();
        return await d.Pieces.RunAsync();
    }

    [Fact]
    public void The_removal_piece_only_takes_good_keys_and_a_piece_that_is_not_one_is_refused()
    {
        var store = new MemoryHistoryStore();
        var good = new string('c', 32);
        var piece = HistoryPieces.Build(MakeRemoved(good, "not a key", new string('D', 32)), HistoryPieces.RemovedName(Room))!;
        Assert.Equal(1, HistoryPieces.Take(store, HistoryPieces.RemovedName(Room), piece));      // only the good key is kept
        Assert.Equal(new[] { good }, store.RemovedKeys(Room));
        Assert.Equal(-1, HistoryPieces.Take(store, HistoryPieces.RemovedName(Room), new byte[] { 1, 2, 3 }));
        Assert.Equal(-1, HistoryPieces.Take(store, "x-bad name", piece));
    }

    private static MemoryHistoryStore MakeRemoved(params string[] keys)
    {
        var store = new MemoryHistoryStore();
        store.ApplyRemoved(Room, keys);
        return store;
    }

    [Fact]
    public async Task Taking_history_in_tells_the_room_that_shows_it()
    {
        using var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice");
        var room = await node.JoinRoomAsync("lobby");
        int raised = 0;
        room.HistoryChanged += () => raised++;
        node.HistoryChangedBySync(new[] { (false, room.Key), (false, "no such room"), (true, new string('9', 64)) });
        Assert.Equal(1, raised);
        Assert.Equal((false, Room), HistoryPieces.WhoOf(HistoryPieces.RemovedName(Room)));
        Assert.Equal((false, Room), HistoryPieces.WhoOf(HistoryPieces.RoomName(Room, "202611")));
        Assert.Equal((true, Room), HistoryPieces.WhoOf(HistoryPieces.DirectName(Room, "202611")));
        Assert.Null(HistoryPieces.WhoOf("g-whatever"));
    }
}
