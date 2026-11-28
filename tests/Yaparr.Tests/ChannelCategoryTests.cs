using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class ChannelCategoryTests
{
    private sealed record Trio(ChatNode Owner, ChatNode B, ChatNode C, ChatRoom Ro, ChatRoom Rb, ChatRoom Rc);

    private static async Task<Trio> StartTrio()
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "owner");
        var b = TestUtil.NewNode(hub, "bob");
        var c = TestUtil.NewNode(hub, "carol");
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 2 && rb.Members.Count >= 2 && rc.Members.Count >= 2, 30_000);
        return new Trio(a, b, c, ra, rb, rc);
    }

    private static string CategoryOf(ChatRoom room, string id) => room.Channels.Single(c => c.Id == id).Category;

    [Fact]
    public async Task An_admin_puts_a_channel_in_a_category_and_everyone_sees_it()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var chess = t.Ro.CreateChannel("chess", ChannelKind.Text);
        var lounge = t.Ro.CreateChannel("Lounge", ChannelKind.Voice);
        await TestUtil.WaitFor(() => t.Rc.Channels.Any(c => c.Id == lounge.Id), 20_000);
        Assert.Empty(t.Rc.Categories);

        t.Ro.SetChannelCategory(chess.Id, "Games");
        t.Ro.SetChannelCategory(lounge.Id, "Games");
        await TestUtil.WaitFor(() => CategoryOf(t.Rc, chess.Id) == "Games" && CategoryOf(t.Rb, lounge.Id) == "Games", 20_000);
        Assert.Equal(new[] { "Games" }, t.Rc.Categories);

        Assert.Throws<InvalidOperationException>(() => t.Rb.SetChannelCategory(chess.Id, "Mine"));
        Assert.Throws<InvalidOperationException>(() => t.Ro.SetChannelCategory(RoomChannels.DefaultText, "Games"));
        Assert.Throws<KeyNotFoundException>(() => t.Ro.SetChannelCategory("1234567890abcdef", "Games"));

        // Out of the category again.
        t.Ro.SetChannelCategory(chess.Id, "");
        await TestUtil.WaitFor(() => CategoryOf(t.Rc, chess.Id) == "", 20_000);
        Assert.Equal(new[] { "Games" }, t.Rc.Categories);   // the voice channel is still in it
    }

    [Fact]
    public async Task A_new_channel_can_be_made_in_a_category_and_a_rename_keeps_its_category()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var chess = t.Ro.CreateChannel("chess", ChannelKind.Text, "Games");
        await TestUtil.WaitFor(() => t.Rc.Channels.Any(c => c.Id == chess.Id), 20_000);
        Assert.Equal("Games", CategoryOf(t.Rc, chess.Id));

        t.Ro.RenameChannel(chess.Id, "board games");
        await TestUtil.WaitFor(() => t.Rc.Channels.Any(c => c.Name == "board games"), 20_000);
        Assert.Equal("Games", CategoryOf(t.Rc, chess.Id));
        t.Ro.DeleteChannel(chess.Id);
        await TestUtil.WaitFor(() => t.Rc.Channels.All(c => c.Id != chess.Id), 20_000);
        Assert.Empty(t.Rc.Categories);   // a category is there while a channel is in it
    }

    [Fact]
    public async Task A_category_is_renamed_and_removed_for_everyone_and_a_name_is_the_same_whatever_its_capitals()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var a = t.Ro.CreateChannel("alpha", ChannelKind.Text, "Games");
        var b = t.Ro.CreateChannel("beta", ChannelKind.Text, "games");   // the room already writes it "Games"
        var other = t.Ro.CreateChannel("other", ChannelKind.Text, "News");
        Assert.Equal("Games", CategoryOf(t.Ro, b.Id));
        await TestUtil.WaitFor(() => t.Rc.Channels.Any(c => c.Id == other.Id), 20_000);

        Assert.Throws<InvalidOperationException>(() => t.Ro.RenameCategory("Games", "NEWS"));
        Assert.Throws<ArgumentException>(() => t.Ro.RenameCategory("Games", "  "));
        Assert.Throws<KeyNotFoundException>(() => t.Ro.RenameCategory("Nothing", "Anything"));
        t.Ro.RenameCategory("Games", "Play");
        await TestUtil.WaitFor(() => CategoryOf(t.Rc, a.Id) == "Play" && CategoryOf(t.Rc, b.Id) == "Play", 20_000);
        Assert.Equal(new[] { "Play", "News" }, t.Rc.Categories);

        t.Ro.DeleteCategory("Play");
        await TestUtil.WaitFor(() => CategoryOf(t.Rc, a.Id) == "" && CategoryOf(t.Rc, b.Id) == "", 20_000);
        Assert.Equal(new[] { "News" }, t.Rc.Categories);
        Assert.Contains(t.Rc.Channels, c => c.Id == a.Id);   // the channels are still there
    }

    [Fact]
    public async Task Categories_move_up_and_down_as_runs_and_channels_move_within_their_category()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var loose = t.Ro.CreateChannel("loose", ChannelKind.Text);
        var g1 = t.Ro.CreateChannel("g1", ChannelKind.Text, "Games");
        var n1 = t.Ro.CreateChannel("n1", ChannelKind.Text, "News");
        var g2 = t.Ro.CreateChannel("g2", ChannelKind.Text, "Games");
        var n2 = t.Ro.CreateChannel("n2", ChannelKind.Voice, "News");
        Assert.Equal(new[] { "Games", "News" }, t.Ro.Categories);

        t.Ro.MoveCategory("News", up: true);
        Assert.Equal(new[] { "News", "Games" }, t.Ro.Categories);
        // Each category is one run in the room's order, after the channels that are in none.
        var ids = t.Ro.Channels.Where(c => !c.IsDefault).Select(c => c.Id).ToList();
        Assert.Equal(new[] { loose.Id, n1.Id, n2.Id, g1.Id, g2.Id }, ids);
        await TestUtil.WaitFor(() => t.Rc.Categories.SequenceEqual(new[] { "News", "Games" }), 20_000);

        t.Ro.MoveCategory("News", up: true);   // already first: nothing happens
        Assert.Equal(new[] { "News", "Games" }, t.Ro.Categories);

        // A channel moves among the channels of its kind in its own category only.
        t.Ro.MoveChannel(g2.Id, up: true);
        Assert.Equal(new[] { g2.Id, g1.Id }, t.Ro.Channels.Where(c => c.Category == "Games").Select(c => c.Id));
        Assert.Equal(new[] { n1.Id, n2.Id }, t.Ro.Channels.Where(c => c.Category == "News").Select(c => c.Id));
        Assert.Throws<InvalidOperationException>(() => t.Rb.MoveCategory("News", up: false));
    }

    [Fact]
    public void A_statement_with_a_category_is_signed_with_it_and_one_without_is_what_it_always_was()
    {
        var identity = PeerIdentity.Generate();
        const string room = "room-key";
        var plain = SignedChannel.Create(identity, room, "0123456789abcdef", "games", ChannelKind.Text, 2, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Null(plain.Category);
        Assert.True(SignedChannel.Verify(plain, room, out _));

        var filed = SignedChannel.Create(identity, room, "0123456789abcdef", "games", ChannelKind.Text, 2, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "Games");
        Assert.Equal("Games", filed.Category);
        Assert.True(SignedChannel.Verify(filed, room, out _));
        Assert.False(SignedChannel.Verify(filed with { Category = "Other" }, room, out _));
        Assert.False(SignedChannel.Verify(filed with { Category = null }, room, out _));   // taking the category off a signed statement breaks it
        Assert.False(SignedChannel.Verify(plain with { Category = "Games" }, room, out _));   // so does putting one on
        Assert.False(SignedChannel.Verify(filed with { Category = new string('x', RoomChannels.MaxCategoryName + 1) }, room, out _));
    }

    [Fact]
    public void A_category_name_is_cleaned_and_the_categories_are_ordered_by_their_first_channel()
    {
        Assert.Equal("Board games", RoomChannels.CleanCategory("  Board \t games  "));
        Assert.Equal("", RoomChannels.CleanCategory(null));
        Assert.Equal(RoomChannels.MaxCategoryName, RoomChannels.CleanCategory(new string('a', 100)).Length);

        ChannelInfo C(string id, int order, string category) => new(id, id, ChannelKind.Text, order, false, category);
        var channels = new[] { C("a", 5, "Zed"), C("b", 3, "alpha"), C("c", 9, "ZED"), C("d", 1, ""), C("e", 4, "Zed") };
        Assert.Equal(new[] { "alpha", "Zed" }, RoomChannels.CategoriesOf(channels));
    }
}
