using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class WatchPlaylistTests
{
    private sealed record Trio(ChatNode A, ChatNode B, ChatNode C, ChatRoom Ra, ChatRoom Rb, ChatRoom Rc, InMemoryDiscoveryHub Hub, string Invite);

    private static async Task<Trio> StartTrio(string? soundboardA = null)
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "alice", soundboardDirectory: soundboardA);
        var b = TestUtil.NewNode(hub, "bob");
        var c = TestUtil.NewNode(hub, "carol");
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 2 && rb.Members.Count >= 2 && rc.Members.Count >= 2, 30_000);
        return new Trio(a, b, c, ra, rb, rc, hub, key.ToString());
    }

    private static PlaylistDraft Film(string id, string title = "A film") => new(id, title, "2001", "movie", 5_400_000_000, null);
    private static PlaylistDraft Episode(string id, string title) => new(id, title, "S1E1 · A show", "episode", 1_500_000_000, "series-1");

    [Fact]
    public async Task The_streamer_keeps_the_list_and_everyone_in_the_room_sees_it_in_order()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        Assert.Throws<InvalidOperationException>(() => t.Ra.AddToPlaylist(Film("f1")));   // not streaming yet
        Assert.Empty(t.Rc.Playlist.Items);

        t.Ra.StartHostingPlaylist("server-1");
        var first = t.Ra.AddToPlaylist(Film("f1", "First"));
        var second = t.Ra.AddToPlaylist(Episode("e1", "Second"));
        await TestUtil.WaitFor(() => t.Rc.Playlist.Items.Count == 2 && t.Rb.Playlist.Items.Count == 2, 20_000);
        Assert.Equal(new[] { "First", "Second" }, t.Rc.Playlist.Items.Select(i => i.Title));
        Assert.Equal("server-1", t.Rc.Playlist.ServerId);
        Assert.Equal("alice", t.Rc.Playlist.HostName);
        Assert.Equal(t.A.Id.Value, t.Rc.Playlist.HostId);   // who to send requests to
        Assert.Equal(t.A.AccountId.Value, first.AddedBy);
        Assert.True(second.IsEpisode);
        Assert.False(t.Rc.Playlist.OthersMayAdd);

        // Others cannot change it themselves.
        Assert.Throws<InvalidOperationException>(() => t.Rb.AddToPlaylist(Film("f2")));
        Assert.Throws<InvalidOperationException>(() => t.Rb.ClearPlaylist());
        Assert.Throws<InvalidOperationException>(() => t.Rb.MoveInPlaylist(first.Id, up: false));
    }

    [Fact]
    public async Task The_streamer_reorders_removes_and_clears()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        t.Ra.StartHostingPlaylist("server-1");
        var one = t.Ra.AddToPlaylist(Film("f1", "One"));
        var two = t.Ra.AddToPlaylist(Film("f2", "Two"));
        var three = t.Ra.AddToPlaylist(Film("f3", "Three"));

        t.Ra.MoveInPlaylist(three.Id, up: true);
        Assert.Equal(new[] { "One", "Three", "Two" }, t.Ra.Playlist.Items.Select(i => i.Title));
        t.Ra.MoveInPlaylist(one.Id, up: true);   // already first: nothing happens
        t.Ra.MoveInPlaylist(two.Id, up: false);   // already last
        Assert.Equal(new[] { "One", "Three", "Two" }, t.Ra.Playlist.Items.Select(i => i.Title));
        await TestUtil.WaitFor(() => t.Rc.Playlist.Items.Select(i => i.Title).SequenceEqual(new[] { "One", "Three", "Two" }), 20_000);

        t.Ra.RemoveFromPlaylist(three.Id);
        await TestUtil.WaitFor(() => t.Rb.Playlist.Items.Count == 2, 20_000);
        Assert.Equal(new[] { "One", "Two" }, t.Rb.Playlist.Items.Select(i => i.Title));

        t.Ra.ClearPlaylist();
        await TestUtil.WaitFor(() => t.Rb.Playlist.Items.Count == 0 && t.Rc.Playlist.Items.Count == 0, 20_000);
    }

    [Fact]
    public async Task Others_add_only_when_the_streamer_allows_it_and_remove_only_what_they_added()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        t.Ra.StartHostingPlaylist("server-1");
        var mine = t.Ra.AddToPlaylist(Film("f0", "Streamers pick"));

        // Not allowed yet: the request is ignored.
        t.Rb.AskToAdd(t.A.Id, Film("f1", "Bobs pick"));
        await Task.Delay(1500);
        Assert.Single(t.Ra.Playlist.Items);

        t.Ra.SetOthersMayAddToPlaylist(true);
        await TestUtil.WaitFor(() => t.Rb.Playlist.OthersMayAdd && t.Rc.Playlist.OthersMayAdd, 20_000);
        t.Rb.AskToAdd(t.A.Id, Film("f1", "Bobs pick"));
        t.Rc.AskToAdd(t.A.Id, Episode("e1", "Carols pick"));
        await TestUtil.WaitFor(() => t.Rb.Playlist.Items.Count == 3 && t.Rc.Playlist.Items.Count == 3, 20_000);
        var bobs = t.Rb.Playlist.Items.Single(i => i.Title == "Bobs pick");
        var carols = t.Rb.Playlist.Items.Single(i => i.Title == "Carols pick");
        Assert.Equal(t.B.AccountId.Value, bobs.AddedBy);
        Assert.Equal("bob", bobs.AddedByName);
        Assert.Equal("server-1", bobs.ServerId);   // the streamer says which library it is in

        // Bob cannot remove Carol's or the streamer's, but can remove his own.
        t.Rb.AskToRemove(t.A.Id, carols.Id);
        t.Rb.AskToRemove(t.A.Id, mine.Id);
        await Task.Delay(1500);
        Assert.Equal(3, t.Ra.Playlist.Items.Count);
        t.Rb.AskToRemove(t.A.Id, bobs.Id);
        await TestUtil.WaitFor(() => t.Rc.Playlist.Items.Count == 2, 20_000);
        Assert.DoesNotContain(t.Rc.Playlist.Items, i => i.Title == "Bobs pick");

        // The streamer removes anyone's.
        t.Ra.RemoveFromPlaylist(carols.Id);
        await TestUtil.WaitFor(() => t.Rb.Playlist.Items.Count == 1, 20_000);

        // Turned off again: no more.
        t.Ra.SetOthersMayAddToPlaylist(false);
        await TestUtil.WaitFor(() => !t.Rb.Playlist.OthersMayAdd, 20_000);
        t.Rb.AskToAdd(t.A.Id, Film("f9", "Too late"));
        await Task.Delay(1500);
        Assert.Single(t.Ra.Playlist.Items);
    }

    [Fact]
    public async Task A_request_to_someone_who_is_not_streaming_does_nothing()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        t.Rb.AskToAdd(t.A.Id, Film("f1"));
        await Task.Delay(1500);
        Assert.Empty(t.Ra.Playlist.Items);
    }

    [Fact]
    public async Task Others_browse_the_streamers_library_through_the_streamer_and_only_when_allowed()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        t.Ra.StartHostingPlaylist("server-1");
        t.Ra.PlaylistBrowseRequested += request => request.Respond("answer to " + request.Body + " for " + request.Who);

        // Not allowed: the streamer's app is not even asked, and the answer says so.
        var refused = await t.Rb.AskLibraryAsync(t.A.Id, "libraries");
        Assert.Contains("does not let others", refused);

        t.Ra.SetOthersMayAddToPlaylist(true);
        await TestUtil.WaitFor(() => t.Rb.Playlist.OthersMayAdd, 20_000);
        Assert.Equal("answer to libraries for bob", await t.Rb.AskLibraryAsync(t.A.Id, "libraries"));
        Assert.Equal("answer to search:x for carol", await t.Rc.AskLibraryAsync(t.A.Id, "search:x"));
    }

    [Fact]
    public async Task A_question_with_no_one_to_answer_it_ends_when_cancelled()
    {
        var t = await StartTrio();
        using var _ = t.A; using var __ = t.B; using var ___ = t.C;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Rb.AskLibraryAsync(t.A.Id, "libraries", cancel.Token));
    }

    [Fact]
    public async Task Someone_who_joins_later_gets_the_list_and_it_is_kept_for_the_next_stream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-plist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var t = await StartTrio(soundboardA: dir);
            using var _ = t.A; using var __ = t.B; using var ___ = t.C;
            t.Ra.StartHostingPlaylist("server-1");
            t.Ra.AddToPlaylist(Film("f1", "First"));
            t.Ra.AddToPlaylist(Film("f2", "Second"));
            t.Ra.StopHostingPlaylist();   // the stream ended

            var d = TestUtil.NewNode(t.Hub, "dave");
            using var ____ = d;
            var rd = await d.JoinRoomAsync(RoomKey.Parse(t.Invite), "lobby");
            await TestUtil.WaitFor(() => rd.Playlist.Items.Count == 2, 30_000);
            Assert.Equal(new[] { "First", "Second" }, rd.Playlist.Items.Select(i => i.Title));

            // Another member streams next: they take the list over as it is, and others may not add until they allow it.
            t.Ra.StartHostingPlaylist("server-1");
            t.Ra.SetOthersMayAddToPlaylist(true);
            await TestUtil.WaitFor(() => rd.Playlist.OthersMayAdd, 20_000);
            await TestUtil.WaitFor(() => t.Rb.Playlist.OthersMayAdd, 20_000);
            t.Rb.StartHostingPlaylist("server-2");
            Assert.Equal(new[] { "First", "Second" }, t.Rb.Playlist.Items.Select(i => i.Title));
            Assert.False(t.Rb.Playlist.OthersMayAdd);
            Assert.Equal("server-2", t.Rb.Playlist.ServerId);
            await TestUtil.WaitFor(() => rd.Playlist.ServerId == "server-2" && !rd.Playlist.OthersMayAdd, 20_000);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task The_list_is_remembered_across_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-plist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new InMemoryDiscoveryHub();
            var identity = PeerIdentity.Generate();
            string invite;
            using (var a = TestUtil.NewNode(hub, "alice", identity: identity, soundboardDirectory: dir))
            {
                var key = RoomKey.Generate(a.AccountId);
                invite = key.ToString();
                var room = await a.JoinRoomAsync(key, "lobby");
                room.StartHostingPlaylist("server-1");
                room.AddToPlaylist(Film("f1", "Kept"));
            }
            using var again = TestUtil.NewNode(hub, "alice", identity: identity, soundboardDirectory: dir);
            var back = await again.JoinRoomAsync(RoomKey.Parse(invite), "lobby");
            Assert.Equal(new[] { "Kept" }, back.Playlist.Items.Select(i => i.Title));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void The_streamers_list_and_a_request_are_signed_and_cannot_be_changed()
    {
        var identity = PeerIdentity.Generate();
        const string room = "room-key";
        var item = new PlaylistItem("e1", "s", "item", "Title", "", "movie", 10, null, "acct", "Alice", 1);
        var state = SignedPlaylist.Create(identity, room, "Alice", "s", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), false, new[] { item });
        Assert.True(SignedPlaylist.Verify(state, room));
        Assert.False(SignedPlaylist.Verify(state, "another-room"));
        Assert.False(SignedPlaylist.Verify(state with { OthersMayAdd = true }, room));
        Assert.False(SignedPlaylist.Verify(state with { Items = new[] { item with { Title = "Other" } } }, room));
        Assert.False(SignedPlaylist.Verify(state with { Items = new[] { item, item } }, room));   // the same entry twice
        Assert.False(SignedPlaylist.Verify(state with { Items = new[] { item with { Kind = "music" } } }, room));
        Assert.False(SignedPlaylist.Verify(state with { Version = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }, room));
        Assert.False(SignedPlaylist.Verify(state with { Items = Enumerable.Range(0, PlaylistLimits.MaxItems + 1).Select(n => item with { Id = "e" + n }).ToArray() }, room));

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ask = SignedAsk.Create(identity, room, "host", "ask-1", "add", "Bob", "{}", now);
        Assert.True(SignedAsk.Verify(ask, room, out var account, out var device));
        Assert.Equal(identity.Id, device);
        Assert.Equal(identity.AccountId, account);
        Assert.False(SignedAsk.Verify(ask with { To = "someone-else" }, room, out _, out _));
        Assert.False(SignedAsk.Verify(ask with { Op = "remove" }, room, out _, out _));
        Assert.False(SignedAsk.Verify(ask with { Body = "{\"x\":1}" }, room, out _, out _));
        Assert.False(SignedAsk.Verify(ask with { Op = "delete" }, room, out _, out _));
        Assert.False(SignedAsk.Verify(SignedAsk.Create(identity, room, "host", "ask-2", "add", "Bob", "{}", now - 3_600_000), room, out _, out _));   // an old one is not replayed
    }
}
