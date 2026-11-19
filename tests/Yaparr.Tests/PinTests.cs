using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class PinOrderTests
{
    [Fact]
    public void Only_positions_that_leave_room_between_them_are_valid()
    {
        Assert.True(PinOrder.IsValid("i"));
        Assert.True(PinOrder.IsValid("0i"));
        Assert.True(PinOrder.IsValid("zz9"));
        Assert.False(PinOrder.IsValid(null));
        Assert.False(PinOrder.IsValid(""));
        Assert.False(PinOrder.IsValid("0"));       // nothing can go before it
        Assert.False(PinOrder.IsValid("i0"));      // ends in a zero
        Assert.False(PinOrder.IsValid("I"));       // capitals are not digits here
        Assert.False(PinOrder.IsValid("i-"));
        Assert.False(PinOrder.IsValid(new string('i', PinOrder.MaxLength + 1)));
    }

    [Fact]
    public void A_position_can_be_made_with_nothing_else_pinned()
    {
        var first = PinOrder.Between(null, null);
        Assert.True(PinOrder.IsValid(first));
    }

    [Fact]
    public void Pinning_one_more_goes_after_the_last_and_moving_to_the_top_goes_before_the_first()
    {
        var one = PinOrder.Between(null, null);
        var after = PinOrder.Between(one, null);
        var before = PinOrder.Between(null, one);
        Assert.True(string.CompareOrdinal(before, one) < 0);
        Assert.True(string.CompareOrdinal(one, after) < 0);
        Assert.True(PinOrder.IsValid(before) && PinOrder.IsValid(after));
    }

    [Fact]
    public void A_position_always_lands_strictly_between_its_neighbours_wherever_it_is_put()
    {
        foreach (int seed in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var rnd = new Random(seed);
            var list = new List<string>();
            for (int i = 0; i < 300; i++)
            {
                int at = rnd.Next(list.Count + 1);
                var made = PinOrder.Between(at > 0 ? list[at - 1] : null, at < list.Count ? list[at] : null);
                Assert.True(PinOrder.IsValid(made));
                list.Insert(at, made);
            }
            Assert.Equal(list.OrderBy(p => p, StringComparer.Ordinal), list);
            Assert.Equal(list.Count, list.Distinct().Count());
        }
    }

    [Fact]
    public void Pinning_or_moving_to_the_top_over_and_over_keeps_positions_short()
    {
        var down = new List<string>();
        for (int i = 0; i < 500; i++) down.Add(PinOrder.Between(down.Count > 0 ? down[^1] : null, null));
        Assert.Equal(down.OrderBy(p => p, StringComparer.Ordinal), down);
        Assert.True(down.Max(p => p.Length) < 40);

        var up = new List<string>();
        for (int i = 0; i < 500; i++) up.Insert(0, PinOrder.Between(null, up.Count > 0 ? up[0] : null));
        Assert.Equal(up.OrderBy(p => p, StringComparer.Ordinal), up);
        Assert.True(up.Max(p => p.Length) < 40);
    }

    [Fact]
    public void Positions_next_to_each_other_or_with_zeros_inside_still_have_room_between_them()
    {
        foreach (var (low, high) in new (string?, string?)[] { ("a", "a05"), ("a0z", "a1"), ("1", "10001"), ("0i", "1"), ("z", "zi"), ("zz", null), (null, "01"), (null, "1") })
        {
            var made = PinOrder.Between(low, high);
            Assert.True(PinOrder.IsValid(made));
            if (low != null) Assert.True(string.CompareOrdinal(low, made) < 0);
            if (high != null) Assert.True(string.CompareOrdinal(made, high) < 0);
        }
    }

    [Fact]
    public void Positions_in_the_wrong_order_or_not_positions_are_refused()
    {
        Assert.Throws<ArgumentException>(() => PinOrder.Between("j", "i"));
        Assert.Throws<ArgumentException>(() => PinOrder.Between("i", "i"));
        Assert.Throws<ArgumentException>(() => PinOrder.Between("i0", null));
        Assert.Throws<ArgumentException>(() => PinOrder.Between(null, "I"));
    }
}

public class PinnedFirstTests
{
    [Fact]
    public void UnpinnedGoNewestFirstBelowThePinned()
    {
        var pins = new PinnedFirst(new[] { RoomPin("b", "a") });
        var at = new Dictionary<string, int> { ["a"] = 1, ["b"] = 0, ["c"] = 5, ["d"] = 3 };
        var arranged = PinnedFirst.Arrange(new[] { "a", "b", "c", "d" }, id => pins.RankOf(true, id), id => DateTimeOffset.FromUnixTimeSeconds(at[id]));
        Assert.Equal(new[] { "b", "c", "d", "a" }, arranged);
    }

    private static PinnedConversation RoomPin(string id, string order) => new(true, id, order);
    private static PinnedConversation ChatPin(string id, string order) => new(false, id, order);

    [Fact]
    public void Pinned_ones_come_first_in_their_own_order_and_the_others_keep_theirs()
    {
        var pins = new PinnedFirst(new[] { RoomPin("d", "a"), RoomPin("b", "b") });
        var arranged = PinnedFirst.Arrange(new[] { "a", "b", "c", "d", "e" }, id => pins.RankOf(true, id));
        Assert.Equal(new[] { "d", "b", "a", "c", "e" }, arranged);
    }

    [Fact]
    public void A_room_and_a_chat_with_the_same_id_are_not_the_same_pin()
    {
        var pins = new PinnedFirst(new[] { RoomPin("x", "i") });
        Assert.True(pins.IsPinned(true, "x"));
        Assert.False(pins.IsPinned(false, "x"));
        Assert.Null(pins.RankOf(true, "y"));
        Assert.Null(pins.RankOf(true, null));
    }

    [Fact]
    public void Putting_a_list_in_order_moves_its_items_and_does_not_rebuild_it()
    {
        var pins = new PinnedFirst(new[] { RoomPin("c", "a"), RoomPin("a", "b") });
        var list = new ObservableCollection<string> { "a", "b", "c", "d" };
        var actions = new List<NotifyCollectionChangedAction>();
        list.CollectionChanged += (_, e) => actions.Add(e.Action);
        PinnedFirst.Apply(list, id => pins.RankOf(true, id));
        Assert.Equal(new[] { "c", "a", "b", "d" }, list);
        Assert.NotEmpty(actions);
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Move, a));

        actions.Clear();
        PinnedFirst.Apply(list, id => pins.RankOf(true, id));   // already in order: nothing moves
        Assert.Empty(actions);
    }

    [Fact]
    public void A_moved_pin_is_put_between_the_neighbours_it_will_have()
    {
        var shown = new[] { RoomPin("a", "d"), RoomPin("b", "h"), RoomPin("c", "p") };
        Assert.Equal((null, "d"), PinnedFirst.Neighbours(shown, shown[2], 0));        // c to the top
        Assert.Equal(("d", "p"), PinnedFirst.Neighbours(shown, shown[1], 1));         // b stays in the middle
        Assert.Equal(("h", "p"), PinnedFirst.Neighbours(shown, shown[0], 1));         // a down one: after b, before c
        Assert.Equal(("p", null), PinnedFirst.Neighbours(shown, shown[0], 2));        // a to the bottom
        Assert.Equal(("p", null), PinnedFirst.Neighbours(shown, shown[0], 99));       // past the end is the end
        Assert.Equal((null, "h"), PinnedFirst.Neighbours(shown, shown[0], -5));       // before the start is the start
    }

    [Fact]
    public void A_chat_and_a_room_with_the_same_id_are_told_apart_when_moving()
    {
        var shown = new[] { RoomPin("x", "d"), ChatPin("x", "h") };
        Assert.Equal(("d", null), PinnedFirst.Neighbours(shown, shown[1], 1));
        Assert.Equal((null, "h"), PinnedFirst.Neighbours(shown, shown[0], 0));
    }
}

public class PinSyncTests
{
    private static readonly string A = new('a', 64), B = new('b', 64), C = new('c', 64), D = new('d', 64);

    // Two devices of one account that know each other (they share a room, which is how devices find each other), as in the multi-device tests.
    private sealed class Pair : IDisposable
    {
        private readonly PeerIdentity _firstId = PeerIdentity.Generate();
        private readonly PeerIdentity _secondId;
        public readonly InMemoryDiscoveryHub Hub = new();
        public ChatNode First = null!, Second = null!;

        public Pair() => _secondId = _firstId.CreateDevice();

        public static async Task<Pair> StartAsync()
        {
            var p = new Pair();
            p.First = TestUtil.NewNode(p.Hub, "alice", identity: p._firstId);
            p.Second = TestUtil.NewNode(p.Hub, "alice", identity: p._secondId);
            var r1 = await p.First.JoinRoomAsync("lobby");
            var r2 = await p.Second.JoinRoomAsync("lobby");
            await TestUtil.WaitFor(() => r1.Members.Count == 1 && r2.Members.Count == 1);
            await TestUtil.WaitFor(() => p.First.IsMyDevice(p.Second.Id) && p.Second.IsMyDevice(p.First.Id));
            return p;
        }

        public void Dispose()
        {
            First.Dispose(); Second.Dispose();
            _firstId.Dispose(); _secondId.Dispose();
        }
    }

    private static IEnumerable<(bool IsRoom, string Id)> Order(ChatNode node) => node.Pins.Select(p => (p.IsRoom, p.Id)).ToList();

    [Fact]
    public async Task A_pin_made_on_one_device_is_on_the_other_and_so_are_moves_and_unpins()
    {
        using var d = await Pair.StartAsync();
        int heard = 0;
        d.Second.PinsChanged += () => Interlocked.Increment(ref heard);

        d.First.Pin(true, A);
        d.First.Pin(true, B);
        d.First.Pin(false, D);
        await TestUtil.WaitFor(() => d.Second.Pins.Count == 3);
        Assert.Equal(new[] { (true, A), (true, B), (false, D) }, Order(d.Second));
        Assert.True(d.Second.IsConversationPinned(true, A));
        Assert.False(d.Second.IsConversationPinned(false, A));   // a room and a chat are different pins
        Assert.True(Volatile.Read(ref heard) > 0);

        // The other device puts A below B.
        var shown = d.Second.Pins;
        var (after, before) = PinnedFirst.Neighbours(shown, shown[0], 1);
        d.Second.MovePin(true, A, after, before);
        await TestUtil.WaitFor(() => Order(d.First).SequenceEqual(new[] { (true, B), (true, A), (false, D) }));

        d.First.Unpin(true, B);
        await TestUtil.WaitFor(() => !d.Second.IsConversationPinned(true, B));
        Assert.Equal(new[] { (true, A), (false, D) }, Order(d.Second));
    }

    [Fact]
    public async Task Pinning_again_after_an_unpin_puts_it_last_and_a_second_pin_changes_nothing()
    {
        using var d = await Pair.StartAsync();
        d.First.Pin(true, A);
        d.First.Pin(true, B);
        d.First.Pin(true, A);   // already pinned
        Assert.Equal(new[] { (true, A), (true, B) }, Order(d.First));
        d.First.Unpin(true, A);
        d.First.Pin(true, A);
        Assert.Equal(new[] { (true, B), (true, A) }, Order(d.First));
        await TestUtil.WaitFor(() => Order(d.Second).SequenceEqual(new[] { (true, B), (true, A) }));
    }

    [Fact]
    public async Task A_pin_moved_on_both_devices_ends_up_where_the_later_move_put_it()
    {
        using var d = await Pair.StartAsync();
        d.First.Pin(true, A);
        d.First.Pin(true, B);
        d.First.Pin(true, C);
        await TestUtil.WaitFor(() => d.Second.Pins.Count == 3);

        d.First.MovePin(true, C, null, d.First.Pins[0].Order);              // C to the top
        await TestUtil.WaitFor(() => Order(d.Second).First() == (true, C));
        d.Second.MovePin(true, C, d.Second.Pins[^1].Order, null);           // then the other device puts it last, later
        await TestUtil.WaitFor(() => Order(d.First).Last() == (true, C));
        Assert.Equal(new[] { (true, A), (true, B), (true, C) }, Order(d.First));
        Assert.Equal(Order(d.First), Order(d.Second));
    }

    [Fact]
    public void Moving_or_unpinning_something_not_pinned_does_nothing_and_bad_ids_are_refused()
    {
        using var id = PeerIdentity.Generate();
        using var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: id);
        int heard = 0;
        node.PinsChanged += () => heard++;
        node.Unpin(true, A);
        node.MovePin(true, A, null, null);
        Assert.Empty(node.Pins);
        Assert.Equal(0, heard);
        Assert.Throws<ArgumentException>(() => node.Pin(true, ""));
        Assert.Throws<ArgumentException>(() => node.Pin(true, "not hex"));
        node.Pin(true, A);
        Assert.Throws<ArgumentException>(() => node.MovePin(true, A, "I0", null));
    }

    [Fact]
    public async Task Leaving_a_room_takes_it_off_the_pins()
    {
        using var id = PeerIdentity.Generate();
        using var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: id);
        var keep = await node.JoinRoomAsync("keep");
        var gone = await node.JoinRoomAsync("gone");
        node.Pin(true, keep.RoomId);
        node.Pin(true, gone.RoomId);
        Assert.Equal(2, node.Pins.Count);

        await gone.LeaveAsync();
        await TestUtil.WaitFor(() => !node.IsConversationPinned(true, gone.RoomId));
        Assert.True(node.IsConversationPinned(true, keep.RoomId));
    }

    [Fact]
    public void Pins_survive_a_restart()
    {
        var hub = new InMemoryDiscoveryHub();
        using var id = PeerIdentity.Generate();
        var path = Path.Combine(Path.GetTempPath(), "yaparr-pins-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var node = TestUtil.NewNode(hub, "alice", identity: id, accountStatePath: path))
            {
                node.Pin(true, A);
                node.Pin(false, D);
                node.Pin(true, B);
                var shown = node.Pins;
                var (after, before) = PinnedFirst.Neighbours(shown, shown[2], 0);
                node.MovePin(true, B, after, before);   // B to the top
            }
            using var back = TestUtil.NewNode(hub, "alice", identity: id, accountStatePath: path);
            Assert.Equal(new[] { (true, B), (true, A), (false, D) }, Order(back));
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}
