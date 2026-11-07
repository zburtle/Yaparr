using System.Buffers.Binary;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class RoomGifTests
{
    // A file that is a PNG as far as its header goes (all the library looks at), made different by its width.
    private static byte[] Png(int width, int padding = 0)
    {
        var b = new byte[40 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 20);
        return b;
    }

    private static async Task<(ChatNode, ChatNode, ChatRoom, ChatRoom)> Pair(InMemoryDiscoveryHub hub)
    {
        var a = TestUtil.NewNode(hub, "alice");
        var b = TestUtil.NewNode(hub, "bob");
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id), 30_000);
        return (a, b, ra, rb);
    }

    [Fact]
    public async Task A_picture_one_member_adds_reaches_the_others_when_they_open_the_shelf_and_newcomers_get_the_list()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        var file = Png(64, 500);
        var added = ra.AddGif(file);
        Assert.True(added.Available);
        Assert.Equal("image/png", added.Mime);
        await TestUtil.WaitFor(() => rb.Gifs.Count == 1, 20_000);
        var seen = rb.Gifs.Single();
        Assert.Equal(a.Id, seen.Owner);
        Assert.Equal("alice", seen.OwnerName);
        // The file is only fetched when the shelf is opened.
        rb.RequestGifs();
        await TestUtil.WaitFor(() => rb.Gifs.Single().Available, 20_000);
        Assert.Equal(file, rb.GetGif(seen.Id));

        using var c = TestUtil.NewNode(hub, "carol");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(ra.Invite), "lobby");
        await TestUtil.WaitFor(() => rc.Gifs.Count == 1, 30_000);
    }

    [Fact]
    public async Task The_same_picture_twice_is_one_entry_and_things_that_are_not_allowed_are_refused()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, _) = await Pair(hub);
        using var _ = a; using var __ = b;

        var first = ra.AddGif(Png(30));
        Assert.Equal(first.Id, ra.AddGif(Png(30)).Id);
        Assert.Single(ra.Gifs);
        Assert.Throws<ArgumentException>(() => ra.AddGif("not a picture"u8.ToArray()));
        var tooBig = Assert.Throws<ArgumentException>(() => ra.AddGif(Png(31, GifLimits.MaxBytes)));
        Assert.Contains("MB", tooBig.Message);
        Assert.Single(ra.Gifs);
    }

    [Fact]
    public async Task A_room_can_hold_any_number_of_pictures()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        for (int i = 0; i < 150; i++) ra.AddGif(Png(100 + i));
        Assert.Equal(150, ra.Gifs.Count);
        await TestUtil.WaitFor(() => rb.Gifs.Count == 150, 30_000);
    }

    private static StoredGif Entry(PeerIdentity who, string roomKey, long? ts = null) =>
        SignedGif.Create(who, roomKey, SignedGif.NewId(), "mallory", new string('b', 64), 1000, "image/png", 10, 10, ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), deleted: false);

    [Fact]
    public void An_entry_is_only_good_when_it_is_well_formed_and_signed_by_its_sender()
    {
        var who = PeerIdentity.Generate();
        var entry = Entry(who, "room");
        Assert.True(SignedGif.Verify(entry, "room"));
        Assert.False(SignedGif.Verify(entry, "another room"));
        Assert.False(SignedGif.Verify(entry with { Hash = new string('c', 64) }, "room"));
        Assert.False(SignedGif.Verify(entry with { Size = GifLimits.MaxBytes + 1 }, "room"));
        Assert.False(SignedGif.Verify(entry with { Mime = "text/html" }, "room"));
        Assert.False(SignedGif.Verify(entry with { W = 0 }, "room"));
        Assert.False(SignedGif.Verify(Entry(who, "room", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()), "room"));
    }

    [Fact]
    public async Task Only_the_person_who_added_a_picture_can_remove_it_and_the_owner_can_take_any_down()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub); // alice owns the room
        using var _ = a; using var __ = b;

        var bobs = rb.AddGif(Png(77));
        await TestUtil.WaitFor(() => ra.Gifs.Count == 1, 20_000);
        Assert.Throws<InvalidOperationException>(() => ra.RemoveGif(bobs.Id));
        Assert.Throws<InvalidOperationException>(() => rb.DeleteGif(bobs.Id)); // not an admin

        ra.DeleteGif(bobs.Id);
        await TestUtil.WaitFor(() => ra.Gifs.Count == 0 && rb.Gifs.Count == 0, 20_000);

        var own = rb.AddGif(Png(88));
        await TestUtil.WaitFor(() => ra.Gifs.Count == 1, 20_000);
        rb.RemoveGif(own.Id);
        await TestUtil.WaitFor(() => ra.Gifs.Count == 0, 20_000);
    }

    [Fact]
    public async Task The_shelf_and_its_files_are_still_there_after_a_restart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "yaparr-gifs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var identity = PeerIdentity.Generate();
            var key = RoomKey.Generate(identity.AccountId);
            var file = Png(99, 300);
            using (var first = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: identity, soundboardDirectory: Path.Combine(folder, "lists"), attachmentDirectory: Path.Combine(folder, "files")))
            {
                var room = await first.JoinRoomAsync(key, "lobby");
                room.AddGif(file);
            }
            using var second = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: identity, soundboardDirectory: Path.Combine(folder, "lists"), attachmentDirectory: Path.Combine(folder, "files"));
            var again = await second.JoinRoomAsync(key, "lobby");
            var gif = Assert.Single(again.Gifs);
            Assert.True(gif.Available);
            Assert.Equal(file, again.GetGif(gif.Id));
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
