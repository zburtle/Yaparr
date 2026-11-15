using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// A device that lacks the older history of a room asks the members for it when someone scrolls back, and the pictures in it come with it.
public class HistoryBackfillTests
{
    private static byte[] Picture(int w, int h)
    {
        var b = new byte[40];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        return b;
    }

    [Fact]
    public async Task Scrolling_back_past_what_a_device_holds_asks_the_members_for_the_older_messages_and_their_pictures()
    {
        var hub = new InMemoryDiscoveryHub();
        using var aliceId = PeerIdentity.Generate();
        using var alice = TestUtil.NewNode(hub, "alice", identity: aliceId);
        using var bob = TestUtil.NewNode(hub, "bob");
        var key = RoomKey.Generate();
        var ra = await alice.JoinRoomAsync(key, "lobby");

        // Alice holds a long history, most of it older than what a newcomer is given when it joins.
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long old = now - (long)TimeSpan.FromDays(30).TotalMilliseconds;
        var picture = alice.PrepareAttachment(Picture(20, 10), "old.png");
        for (int i = 0; i < 150; i++)
        {
            string text = i == 40 ? picture.ToText("a picture from long ago") : $"old {i:000}";
            Assert.True(ra.History.TryAdd(SignedMessage.Create(aliceId, ra.Key, "alice", text, old + i * 1000)));
        }
        for (int i = 0; i < 10; i++) Assert.True(ra.History.TryAdd(SignedMessage.Create(aliceId, ra.Key, "alice", $"recent {i}", now - 20_000 + i * 1000)));

        var rb = await bob.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => rb.Members.Count >= 1 && rb.GetHistory().Count == 10, 30_000);
        var shownAsNews = new List<string>();
        rb.MessageReceived += m => { lock (shownAsNews) shownAsNews.Add(m.Text); };

        // Bob holds only the recent ones: there is nothing earlier on this device until the members are asked.
        var oldest = rb.GetHistory()[0];
        Assert.Empty(rb.GetEarlierHistory(null, oldest.Key, 100).Messages);
        int arrived = 0;
        rb.EarlierHistoryReceived += () => Interlocked.Increment(ref arrived);
        Assert.True(rb.RequestEarlierHistory(null, oldest.Key) >= 1);
        await TestUtil.WaitFor(() => Volatile.Read(ref arrived) > 0, 30_000);
        await Task.Delay(300);   // the rest of the frames

        var page = rb.GetEarlierHistory(null, oldest.Key, 100);
        Assert.True(page.HasMore);
        Assert.Equal(100, page.Messages.Count);
        Assert.Equal("old 149", page.Messages[^1].Text);
        // It was kept, not announced as it arrived.
        lock (shownAsNews) Assert.DoesNotContain(shownAsNews, t => t.StartsWith("old"));

        // The next page: Bob asks again from the oldest message it has now (members answer a request every couple of seconds).
        await Task.Delay(2200);
        int before = Volatile.Read(ref arrived);
        Assert.True(rb.RequestEarlierHistory(null, page.Messages[0].Key) >= 1);
        await TestUtil.WaitFor(() => Volatile.Read(ref arrived) > before, 30_000);
        await Task.Delay(300);
        var next = rb.GetEarlierHistory(null, page.Messages[0].Key, 100);
        Assert.Equal(50, next.Messages.Count);
        Assert.False(next.HasMore);
        Assert.Equal("old 000", next.Messages[0].Text);

        // The picture in an old message is fetched from the member that has it, as the message is read back into view.
        var withPicture = next.Messages.Single(m => m.Attachment != null);
        await TestUtil.WaitFor(() => bob.HasAttachment(withPicture.Attachment!.Hash), 30_000);
    }

    [Fact]
    public async Task A_request_for_earlier_messages_does_nothing_when_nobody_is_connected_or_the_message_is_not_held()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alone = TestUtil.NewNode(hub, "alone");
        var room = await alone.JoinRoomAsync("empty");
        await room.SendAsync("only me");
        var key = room.GetHistory().Single().Key;
        Assert.Equal(0, room.RequestEarlierHistory(null, key));                       // nobody to ask
        Assert.Equal(0, room.RequestEarlierHistory(null, new string('a', 32)));       // not a message this device holds
        Assert.Equal(0, room.RequestEarlierHistory(null, "not a key"));
    }
}
