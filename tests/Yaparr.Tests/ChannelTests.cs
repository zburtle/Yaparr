using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class ChannelTests
{
    private sealed record Trio(ChatNode Owner, ChatNode B, ChatNode C, ChatRoom Ro, ChatRoom Rb, ChatRoom Rc, InMemoryDiscoveryHub Hub, string Invite);

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
        return new Trio(a, b, c, ra, rb, rc, hub, key.ToString());
    }

    private static List<ChatMessage> Collect(ChatRoom room)
    {
        var list = new List<ChatMessage>();
        room.MessageReceived += m => { lock (list) list.Add(m); };
        return list;
    }

    private static ChatMessage? Find(List<ChatMessage> list, string text) { lock (list) return list.FirstOrDefault(m => m.Text == text); }

    [Fact]
    public async Task Every_room_starts_with_a_text_and_a_voice_channel_and_a_room_without_an_owner_cannot_add_more()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        var room = await a.JoinRoomAsync("old room");
        Assert.Equal(new[] { RoomChannels.DefaultText, RoomChannels.DefaultVoice }, room.Channels.Select(c => c.Id));
        Assert.Equal(new[] { ChannelKind.Text, ChannelKind.Voice }, room.Channels.Select(c => c.Kind));
        Assert.Throws<InvalidOperationException>(() => room.CreateChannel("games", ChannelKind.Text));
    }

    [Fact]
    public async Task Admins_add_rename_and_remove_channels_and_everyone_sees_it()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;

        Assert.Throws<InvalidOperationException>(() => t.Rb.CreateChannel("games", ChannelKind.Text));
        var games = t.Ro.CreateChannel("games", ChannelKind.Text);
        var lounge = t.Ro.CreateChannel("Lounge", ChannelKind.Voice);
        await TestUtil.WaitFor(() => t.Rb.Channels.Any(c => c.Id == games.Id) && t.Rc.Channels.Any(c => c.Id == lounge.Id), 20_000);
        Assert.Equal(new[] { "general", "Voice", "games", "Lounge" }, t.Rc.Channels.Select(c => c.Name));
        Assert.Equal(ChannelKind.Voice, t.Rc.Channels.Single(c => c.Id == lounge.Id).Kind);

        // Same name twice in one kind is refused; the same name in the other kind is fine.
        Assert.Throws<InvalidOperationException>(() => t.Ro.CreateChannel("GAMES", ChannelKind.Text));
        t.Ro.CreateChannel("games", ChannelKind.Voice);

        t.Ro.SetAdmin(t.B.AccountId, true);
        await TestUtil.WaitFor(() => t.Rb.MyRole == RoomRole.Admin, 20_000);
        t.Rb.RenameChannel(games.Id, "board games");
        await TestUtil.WaitFor(() => t.Ro.Channels.Any(c => c.Name == "board games") && t.Rc.Channels.Any(c => c.Name == "board games"), 20_000);

        t.Rb.DeleteChannel(games.Id);
        await TestUtil.WaitFor(() => t.Ro.Channels.All(c => c.Id != games.Id) && t.Rc.Channels.All(c => c.Id != games.Id), 20_000);

        // The first two cannot be removed but can be renamed.
        Assert.Throws<InvalidOperationException>(() => t.Ro.DeleteChannel(RoomChannels.DefaultText));
        t.Ro.RenameChannel(RoomChannels.DefaultText, "town square");
        await TestUtil.WaitFor(() => t.Rc.Channels.First().Name == "town square", 20_000);
        Assert.Equal(RoomChannels.DefaultText, t.Rc.Channels.First().Id);
        Assert.Throws<KeyNotFoundException>(() => t.Ro.DeleteChannel("1234567890abcdef"));
    }

    [Fact]
    public async Task A_message_goes_to_the_channel_it_was_sent_to_and_every_member_gets_it()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var games = t.Ro.CreateChannel("games", ChannelKind.Text);
        await TestUtil.WaitFor(() => t.Rb.Channels.Any(c => c.Id == games.Id), 20_000);
        var atCarol = Collect(t.Rc);

        await t.Rb.SendAsync("anyone for chess?", games.Id);
        await t.Rb.SendAsync("hello everyone");
        await TestUtil.WaitFor(() => Find(atCarol, "anyone for chess?") != null && Find(atCarol, "hello everyone") != null, 20_000);
        Assert.Equal(games.Id, Find(atCarol, "anyone for chess?")!.Channel);
        Assert.Equal(RoomChannels.DefaultText, Find(atCarol, "hello everyone")!.Channel);
        Assert.Equal(games.Id, t.Rb.GetHistory().Single(m => m.Text == "anyone for chess?").Channel);

        Assert.Throws<InvalidOperationException>(() => t.Rb.SendAsync("lost", "1234567890abcdef").GetAwaiter().GetResult());
        Assert.Throws<InvalidOperationException>(() => t.Rb.SendAsync("to voice", RoomChannels.DefaultVoice).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Someone_who_joins_later_gets_the_channels_and_what_was_said_in_them()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var games = t.Ro.CreateChannel("games", ChannelKind.Text);
        await TestUtil.WaitFor(() => t.Rb.Channels.Any(c => c.Id == games.Id), 20_000);
        await t.Rb.SendAsync("chess at nine", games.Id);

        var d = TestUtil.NewNode(t.Hub, "dave");
        using var ____ = d;
        var rd = await d.JoinRoomAsync(RoomKey.Parse(t.Invite), "lobby");
        await TestUtil.WaitFor(() => rd.Channels.Any(c => c.Id == games.Id) && rd.GetHistory().Any(m => m.Text == "chess at nine"), 30_000);
        Assert.Equal(games.Id, rd.GetHistory().Single(m => m.Text == "chess at nine").Channel);
    }

    [Fact]
    public async Task A_channel_an_admin_made_goes_when_the_admin_does()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        t.Ro.SetAdmin(t.B.AccountId, true);
        await TestUtil.WaitFor(() => t.Rb.MyRole == RoomRole.Admin && t.Rc.RoleOf(t.B.AccountId) == RoomRole.Admin, 20_000);
        var made = t.Rb.CreateChannel("bobs corner", ChannelKind.Text);
        await TestUtil.WaitFor(() => t.Ro.Channels.Any(c => c.Id == made.Id) && t.Rc.Channels.Any(c => c.Id == made.Id), 20_000);

        t.Ro.SetAdmin(t.B.AccountId, false);
        await TestUtil.WaitFor(() => t.Ro.Channels.All(c => c.Id != made.Id) && t.Rc.Channels.All(c => c.Id != made.Id), 20_000);
    }

    [Fact]
    public async Task The_channels_are_remembered_across_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-chan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new InMemoryDiscoveryHub();
            var identity = PeerIdentity.Generate();
            var key = RoomKey.Generate(identity.AccountId);
            ChatNodeOptions Options() => new()
            {
                Identity = identity, RolesDirectory = dir, DisplayName = "owner",
                DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
                DiscoveryInterval = TimeSpan.FromMilliseconds(300), Network = TestUtil.FastNetwork,
            };
            string id;
            using (var first = new ChatNode(Options()))
            {
                var room = await first.JoinRoomAsync(key, "lobby");
                id = room.CreateChannel("games", ChannelKind.Text).Id;
            }
            using var second = new ChatNode(Options());
            var again = await second.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
            Assert.Contains(again.Channels, c => c.Id == id && c.Name == "games");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_message_is_signed_with_its_channel_and_one_in_the_first_channel_is_signed_as_before()
    {
        var author = PeerIdentity.Generate();
        var first = SignedMessage.Create(author, "room", "alice", "hi", channel: RoomChannels.DefaultText);
        Assert.Null(first.Chan);
        Assert.True(SignedMessage.Verify(first, "room"));

        var other = SignedMessage.Create(author, "room", "alice", "hi", channel: "1234567890abcdef");
        Assert.Equal("1234567890abcdef", other.Chan);
        Assert.True(SignedMessage.Verify(other, "room"));
        Assert.False(SignedMessage.Verify(other with { Chan = "fedcba0987654321" }, "room"));
        Assert.False(SignedMessage.Verify(other with { Chan = null }, "room"));
        Assert.False(SignedMessage.Verify(other with { Chan = "not a channel!!" }, "room"));
    }

    [Fact]
    public void The_history_limit_is_per_channel()
    {
        var author = PeerIdentity.Generate();
        var history = new RoomHistory("room", null, max: 3, TimeSpan.FromDays(1));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 6; i++) Assert.True(history.TryAdd(SignedMessage.Create(author, "room", "a", $"busy {i}", now - 10_000 + i, "1111111111111111")));
        for (int i = 0; i < 2; i++) Assert.True(history.TryAdd(SignedMessage.Create(author, "room", "a", $"quiet {i}", now - 20_000 + i, "2222222222222222")));
        var kept = history.Snapshot().Select(m => m.Text).ToList();
        Assert.Equal(new[] { "busy 3", "busy 4", "busy 5" }, kept.Where(t => t.StartsWith("busy")));
        Assert.Equal(new[] { "quiet 0", "quiet 1" }, kept.Where(t => t.StartsWith("quiet")));
    }

    [Fact]
    public async Task Admins_move_channels_up_and_down_among_their_kind_and_everyone_sees_the_order()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;

        var alpha = t.Ro.CreateChannel("alpha", ChannelKind.Text);
        var beta = t.Ro.CreateChannel("beta", ChannelKind.Text);
        var gamma = t.Ro.CreateChannel("gamma", ChannelKind.Text);
        t.Ro.CreateChannel("Lounge", ChannelKind.Voice);
        List<string> TextNames(ChatRoom room) => room.Channels.Where(c => c.Kind == ChannelKind.Text).Select(c => c.Name).ToList();
        await TestUtil.WaitFor(() => TextNames(t.Rc).Count == 4 && t.Rc.Channels.Count == 6, 20_000);
        Assert.Equal(new[] { "general", "alpha", "beta", "gamma" }, TextNames(t.Rc));

        t.Ro.MoveChannel(gamma.Id, up: true);
        await TestUtil.WaitFor(() => TextNames(t.Rc).SequenceEqual(new[] { "general", "alpha", "gamma", "beta" }), 20_000);
        t.Ro.MoveChannel(gamma.Id, up: true);
        await TestUtil.WaitFor(() => TextNames(t.Rb).SequenceEqual(new[] { "general", "gamma", "alpha", "beta" }), 20_000);

        // At the top already, nothing changes; neither at the bottom. The first channels do not move, and a member who is no admin cannot.
        t.Ro.MoveChannel(gamma.Id, up: true);
        t.Ro.MoveChannel(beta.Id, up: false);
        Assert.Equal(new[] { "general", "gamma", "alpha", "beta" }, TextNames(t.Ro));
        Assert.Throws<InvalidOperationException>(() => t.Ro.MoveChannel(RoomChannels.DefaultText, up: false));
        Assert.Throws<InvalidOperationException>(() => t.Rb.MoveChannel(alpha.Id, up: true));
        Assert.Throws<KeyNotFoundException>(() => t.Ro.MoveChannel("0123456789abcdef", up: true));

        // The voice channels have an order of their own, and the text channels' moves leave them alone.
        var second = t.Ro.CreateChannel("Games", ChannelKind.Voice);
        await TestUtil.WaitFor(() => t.Rc.Channels.Any(c => c.Id == second.Id), 20_000);
        t.Ro.MoveChannel(second.Id, up: true);
        await TestUtil.WaitFor(() => t.Rc.Channels.Where(c => c.Kind == ChannelKind.Voice).Select(c => c.Name).SequenceEqual(new[] { "Voice", "Games", "Lounge" }), 20_000);
        Assert.Equal(new[] { "general", "gamma", "alpha", "beta" }, TextNames(t.Rc));
    }

    [Fact]
    public async Task A_voice_channel_that_is_removed_takes_you_out_of_it()
    {
        var t = await StartTrio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;

        var lounge = t.Ro.CreateChannel("Lounge", ChannelKind.Voice);
        var games = t.Ro.CreateChannel("Games", ChannelKind.Voice);
        await TestUtil.WaitFor(() => t.Rb.Channels.Any(c => c.Id == lounge.Id) && t.Rb.Channels.Any(c => c.Id == games.Id), 20_000);

        // Bob is in one, and the owner is in the other.
        var removedAtBob = new List<string>(); var removedAtOwner = new List<string>();
        t.Rb.VoiceChannelRemoved += id => { lock (removedAtBob) removedAtBob.Add(id); };
        t.Ro.VoiceChannelRemoved += id => { lock (removedAtOwner) removedAtOwner.Add(id); };
        using var bobSession = t.Rb.JoinVoice(lounge.Id);
        using var ownerSession = t.Ro.JoinVoice(games.Id);
        await TestUtil.WaitFor(() => t.Rc.VoiceParticipants.Count(p => p.Channel == lounge.Id) == 1 && t.Rc.VoiceParticipants.Count(p => p.Channel == games.Id) == 1, 20_000);

        // Someone else removes Bob's channel: his session ends by itself and he is told.
        t.Ro.DeleteChannel(lounge.Id);
        await TestUtil.WaitFor(() => { lock (removedAtBob) return removedAtBob.Count == 1; }, 20_000);
        Assert.Equal(lounge.Id, removedAtBob[0]);
        await TestUtil.WaitFor(() => t.Rc.VoiceParticipants.Count(p => p.Channel == lounge.Id) == 0, 20_000);
        Assert.Throws<InvalidOperationException>(() => t.Rb.JoinVoice(lounge.Id));

        // Removing the channel you are in yourself does the same.
        t.Ro.DeleteChannel(games.Id);
        lock (removedAtOwner) Assert.Equal(new[] { games.Id }, removedAtOwner);
        await TestUtil.WaitFor(() => t.Rc.VoiceParticipants.Count(p => p.Channel == games.Id) == 0, 20_000);

        // A channel you are not in changes nothing for you.
        var other = t.Ro.CreateChannel("Other", ChannelKind.Voice);
        await TestUtil.WaitFor(() => t.Rb.Channels.Any(c => c.Id == other.Id), 20_000);
        using var inDefault = t.Rb.JoinVoice(RoomChannels.DefaultVoice);
        t.Ro.DeleteChannel(other.Id);
        await Task.Delay(500);
        lock (removedAtBob) Assert.Single(removedAtBob);
        Assert.Contains(t.Rb.VoiceParticipants, p => p.Channel == RoomChannels.DefaultVoice);
    }
}
