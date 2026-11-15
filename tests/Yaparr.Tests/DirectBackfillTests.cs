using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// A device that lacks the older part of a direct conversation asks the other person's devices (and its own) for it, and checks what comes back.
public class DirectBackfillTests
{
    private static byte[] Picture(int w, int h)
    {
        var b = new byte[40];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        return b;
    }

    // A message as one side of the conversation holds it: written by `author`, signed for the other, with the time it was written.
    private static DirectRecord Held(PeerIdentity me, PeerIdentity other, PeerIdentity author, string id, long ts, string text)
    {
        bool mine = author == me;
        var proof = DirectProof.Create(author, mine ? other.AccountId : me.AccountId, id, ts, text);
        return new DirectRecord(id, other.AccountId.Value, mine ? "me" : "them", mine, ts, text, proof.Pub, proof.Acct, proof.Cert, proof.Sig, ts);
    }

    private static ChatNode Node(InMemoryDiscoveryHub hub, string name, PeerIdentity identity, IChatHistoryStore store) => new(new ChatNodeOptions
    {
        DisplayName = name, Identity = identity, HistoryStore = store, SyncHistory = true,
        DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300), Network = TestUtil.FastNetwork,
    });

    [Fact]
    public async Task Scrolling_back_in_a_conversation_asks_the_other_person_for_the_older_messages_and_checks_them()
    {
        var hub = new InMemoryDiscoveryHub();
        using var aliceId = PeerIdentity.Generate();
        using var bobId = PeerIdentity.Generate();
        long old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)TimeSpan.FromDays(30).TotalMilliseconds;

        // Alice holds a long conversation with Bob, some of it a picture; Bob's device has none of it.
        var aliceStore = new MemoryHistoryStore();
        using var alice = Node(hub, "alice", aliceId, aliceStore);
        var picture = alice.PrepareAttachment(Picture(20, 10), "old.png");
        for (int i = 0; i < 120; i++)
        {
            var author = i % 2 == 0 ? aliceId : bobId;
            var record = Held(aliceId, bobId, author, $"d{i:000}", old + i * 1000, i == 60 ? picture.ToText("an old picture") : $"line {i:000}");
            Assert.True(aliceStore.AddDirect(DirectHistory.Key(record), record, null));
        }
        // Reactions: Bob put a thumbs up on one of Alice's messages, and Alice a party popper on one of Bob's (each names the message by the key its writer knew it by).
        var thumbs = Held(aliceId, bobId, bobId, "r1", old + 100_500, MessageEnvelope.WrapReaction(ChatMessage.KeyOf(aliceId.Id, "d070"), "👍", true));
        var party = Held(aliceId, bobId, aliceId, "r2", old + 101_500, MessageEnvelope.WrapReaction(ChatMessage.KeyOf(bobId.AccountId, "d071"), "🎉", true));
        foreach (var reaction in new[] { thumbs, party }) aliceStore.AddDirect(DirectHistory.Key(reaction), reaction, DirectHistory.ReactionTargetOf(reaction.Text));
        // One message that is forged: signed by Alice's own key but said to be from Bob.
        var forged = Held(aliceId, bobId, aliceId, "forged", old + 500, "Bob said this") with { Mine = false };
        aliceStore.AddDirect(DirectHistory.Key(forged), forged, null);

        using var bob = Node(hub, "bob", bobId, new MemoryHistoryStore());
        var rooms = await Task.WhenAll(alice.JoinRoomAsync("lobby"), bob.JoinRoomAsync("lobby"));
        await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count == 1), 30_000);
        var aliceChat = await alice.OpenDirectChatAsync(bob.AccountId);
        await aliceChat.SendAsync("hello bob");
        await TestUtil.WaitFor(() => bob.GetDirectChat(alice.AccountId).History.Count == 1, 30_000);

        var chat = bob.GetDirectChat(alice.AccountId);
        var hello = chat.History[0];
        Assert.Empty(chat.GetEarlierHistory(hello.Id, 100).Messages);
        int arrived = 0;
        chat.EarlierHistoryReceived += () => Interlocked.Increment(ref arrived);
        var seenAsNews = new List<string>();
        chat.MessageReceived += m => { lock (seenAsNews) seenAsNews.Add(m.Text); };
        Assert.True(chat.RequestEarlierHistory(hello.Id) >= 1);
        await TestUtil.WaitFor(() => Volatile.Read(ref arrived) > 0, 30_000);
        await Task.Delay(300);

        var page = chat.GetEarlierHistory(hello.Id, 100);
        Assert.Equal(99, page.Messages.Count);              // the newest hundred Alice holds, one of them the message Bob has
        Assert.False(page.HasMore);
        Assert.Equal("line 119", page.Messages[^1].Text);
        // Each is on the right side: what Alice wrote is from her, what Bob wrote is from him (his own device, as far as he is concerned).
        Assert.All(page.Messages, m => Assert.Equal(int.Parse(m.Id[1..]) % 2 == 0 ? alice.Id : bob.Id, m.From.Id));
        lock (seenAsNews) Assert.Empty(seenAsNews);
        Assert.DoesNotContain(page.Messages, m => m.Text == "Bob said this");

        // The reactions came with the messages they are on, and are Bob's and Alice's.
        var onAlices = page.Messages.Single(m => m.Id == "d070");
        var onBobs = page.Messages.Single(m => m.Id == "d071");
        var reactions = chat.GetReactions(new[] { onAlices.Key, onBobs.Key });
        var up = Assert.Single(reactions.For(onAlices.Key));
        Assert.Equal("👍", up.Emoji);
        Assert.Equal(bob.AccountId, Assert.Single(up.Accounts));
        var popper = Assert.Single(reactions.For(onBobs.Key));
        Assert.Equal("🎉", popper.Emoji);
        Assert.Equal(alice.AccountId, Assert.Single(popper.Accounts));

        // The rest, asked for from the oldest message now held (members answer one request every couple of seconds).
        await Task.Delay(2200);
        int before = Volatile.Read(ref arrived);
        Assert.True(chat.RequestEarlierHistory(page.Messages[0].Id) >= 1);
        await TestUtil.WaitFor(() => Volatile.Read(ref arrived) > before, 30_000);
        await Task.Delay(300);
        var rest = chat.GetEarlierHistory(page.Messages[0].Id, 100);
        Assert.Equal(21, rest.Messages.Count);
        Assert.Equal("line 000", rest.Messages[0].Text);

        // The picture in the old message is fetched from Alice as it is read back.
        var withPicture = page.Messages.Concat(rest.Messages).Single(m => m.Attachment != null);
        await TestUtil.WaitFor(() => bob.HasAttachment(withPicture.Attachment!.Hash), 30_000);
    }

    [Fact]
    public async Task A_new_device_asks_the_other_devices_of_the_account_for_the_older_part_of_a_conversation()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        using var bobId = PeerIdentity.Generate();
        long old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)TimeSpan.FromDays(30).TotalMilliseconds;

        // The first device holds the whole conversation with Bob; the second has just been added and has nothing.
        var firstStore = new MemoryHistoryStore();
        for (int i = 0; i < 40; i++)
        {
            var author = i % 2 == 0 ? firstId : bobId;
            var record = Held(firstId, bobId, author, $"d{i:000}", old + i * 1000, $"line {i:000}");
            firstStore.AddDirect(DirectHistory.Key(record), record, null);
        }
        using var first = Node(hub, "alice", firstId, firstStore);
        using var second = Node(hub, "alice", secondId, new MemoryHistoryStore());
        using var bob = Node(hub, "bob", bobId, new MemoryHistoryStore());
        var rooms = await Task.WhenAll(first.JoinRoomAsync("lobby"), second.JoinRoomAsync("lobby"), bob.JoinRoomAsync("lobby"));
        await TestUtil.WaitFor(() => rooms.All(r => r.Members.Count == 2), 30_000);

        // A message sent now reaches the second device at once, so it has something to scroll back from.
        var chat = await first.OpenDirectChatAsync(bob.AccountId);
        await chat.SendAsync("hello bob");
        var onSecond = second.GetDirectChat(bob.AccountId);
        await TestUtil.WaitFor(() => onSecond.History.Count == 1, 30_000);

        int arrived = 0;
        var anchor = onSecond.History[0].Id;
        onSecond.EarlierHistoryReceived += () => Interlocked.Increment(ref arrived);
        Assert.True(onSecond.RequestEarlierHistory(anchor) >= 1);
        await TestUtil.WaitFor(() => Volatile.Read(ref arrived) > 0, 30_000);
        await Task.Delay(300);

        var page = onSecond.GetEarlierHistory(anchor, 100);
        Assert.Equal(40, page.Messages.Count);
        Assert.Equal("line 000", page.Messages[0].Text);
        // What the account wrote is its own on this device too; what Bob wrote is his.
        Assert.All(page.Messages, m => Assert.Equal(int.Parse(m.Id[1..]) % 2 == 0, second.IsMyDevice(m.From.Id)));
    }

    [Fact]
    public async Task A_request_does_nothing_when_nobody_is_connected_or_the_message_is_not_held()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alone = TestUtil.NewNode(hub, "alone");
        var chat = alone.GetDirectChat(PeerIdentity.Generate().AccountId);
        Assert.Equal(0, chat.RequestEarlierHistory("nothing"));
        await Task.CompletedTask;
    }
}
