using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class ReplyTests
{
    private static readonly string Key = new string('a', 32);
    private static readonly PeerId Alice = new(new string('1', 64));
    private static readonly PeerId Bob = new(new string('2', 64));

    [Fact]
    public void A_message_with_no_reply_and_no_names_is_sent_as_it_is()
    {
        Assert.Equal("hello", MessageEnvelope.Wrap("hello", null, null));
        Assert.Equal("hello", MessageEnvelope.Wrap("hello", null, Array.Empty<Mention>()));
        var parts = MessageEnvelope.Parse("hello");
        Assert.Equal("hello", parts.Body);
        Assert.Null(parts.ReplyTo);
        Assert.Empty(parts.Mentions);
    }

    [Fact]
    public void A_reply_and_the_names_come_back_with_the_words()
    {
        var text = MessageEnvelope.Wrap("@Alice A and @bob, sure, and thanks\nsecond line", Key, new[] { new Mention(Alice, "Alice A"), new Mention(Bob, "bob"), new Mention(Alice, "again") });
        var parts = MessageEnvelope.Parse(text);
        Assert.Equal("@Alice A and @bob, sure, and thanks\nsecond line", parts.Body);
        Assert.Equal(Key, parts.ReplyTo);
        Assert.Equal(new[] { new Mention(Alice, "Alice A"), new Mention(Bob, "bob") }, parts.Mentions);   // named once each, in order
        Assert.Equal("@Alice A", parts.Mentions[0].Tag);          // the name can hold a space

        var onlyReply = MessageEnvelope.Parse(MessageEnvelope.Wrap("yes", Key, null));
        Assert.Equal("yes", onlyReply.Body);
        Assert.Equal(Key, onlyReply.ReplyTo);
        Assert.Empty(onlyReply.Mentions);

        var onlyNames = MessageEnvelope.Parse(MessageEnvelope.Wrap("@bob look", null, new[] { new Mention(Bob, "bob") }));
        Assert.Null(onlyNames.ReplyTo);
        Assert.Equal(new[] { new Mention(Bob, "bob") }, onlyNames.Mentions);
    }

    [Fact]
    public void Words_that_only_look_like_an_envelope_stay_words_and_bad_parts_are_dropped()
    {
        foreach (var text in new[] { "\u0001yaparr-env1\u0001", "\u0001yaparr-env1\u0001{not json}\u0001hi", "\u0001other\u0001{}\u0001hi", "\u0001", "" })
            Assert.Equal(text, MessageEnvelope.Parse(text).Body);

        // A header with a bad key and a bad account keeps the words and drops what is not valid.
        var bad = MessageEnvelope.Parse("\u0001yaparr-env1\u0001{\"r\":\"zz\",\"m\":[{\"a\":\"nope\",\"n\":\"x\"},{\"a\":\"" + Alice.Value + "\",\"n\":\"al\\nice\"},{\"a\":\"" + Bob.Value + "\",\"n\":\"\"}]}\u0001hi");
        Assert.Equal("hi", bad.Body);
        Assert.Null(bad.ReplyTo);
        Assert.Equal(new[] { new Mention(Alice, "alice") }, bad.Mentions);   // no bad account, no nameless one, no line break in a name

        Assert.Throws<ArgumentException>(() => MessageEnvelope.Wrap("hi", "not a key", null));
    }

    [Fact]
    public void Nobody_can_be_named_more_than_twenty_times_and_the_message_stays_inside_the_limit()
    {
        var many = Enumerable.Range(0, 60).Select(i => new Mention(new PeerId(i.ToString("x2").PadLeft(64, '0')), "p" + i)).ToList();
        Assert.Equal(MessageEnvelope.MaxMentions, MessageEnvelope.Parse(MessageEnvelope.Wrap("hi", null, many)).Mentions.Count);
        Assert.Throws<InvalidOperationException>(() => MessageEnvelope.Wrap(new string('x', MessageEnvelope.MaxText), Key, new[] { new Mention(Alice, "a") }));
    }

    [Fact]
    public void A_message_key_is_the_same_wherever_it_is_read_and_cannot_be_made_by_another_sender()
    {
        var device = PeerIdentity.Generate().Id;
        var message = new ChatMessage(new PeerInfo(device, "x", null), "room", "abc123", "hi", DateTimeOffset.UtcNow);
        Assert.Equal(ChatMessage.KeyOf(device, "abc123"), message.Key);
        Assert.Equal(32, message.Key.Length);
        Assert.NotEqual(ChatMessage.KeyOf(PeerIdentity.Generate().Id, "abc123"), message.Key);   // another sender, the same id
        Assert.Equal("hi", message.Body);
    }

    [Fact]
    public async Task A_reply_and_a_name_reach_the_room_and_a_direct_chat_with_their_meaning()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        var key = RoomKey.Generate(alice.AccountId);
        var ra = await alice.JoinRoomAsync(key, "lobby");
        var rb = await bob.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 1 && rb.Members.Count >= 1, 30_000);

        var atAlice = new List<ChatMessage>(); var atBob = new List<ChatMessage>();
        ra.MessageReceived += m => { lock (atAlice) atAlice.Add(m); };
        rb.MessageReceived += m => { lock (atBob) atBob.Add(m); };

        await ra.SendAsync("who has the notes?");
        await TestUtil.WaitFor(() => { lock (atBob) return atBob.Count == 1; });
        ChatMessage original; lock (atBob) original = atBob[0];

        await rb.SendAsync("@alice I do", null, original.Key, new[] { new Mention(alice.AccountId, "alice") });
        await TestUtil.WaitFor(() => { lock (atAlice) return atAlice.Count == 1; });
        ChatMessage reply; lock (atAlice) reply = atAlice[0];

        Assert.Equal("@alice I do", reply.Body);
        Assert.Equal(original.Key, reply.Parts.ReplyTo);
        Assert.Contains(reply.Parts.Mentions, m => m.Account == alice.AccountId && m.Tag == "@alice");
        Assert.Equal(original.Key, ChatMessage.KeyOf(alice.Id, original.Id));   // the key is what the sender would compute for their own message

        // The reply is part of the message, so it is in the history that is caught up on, too.
        Assert.Contains(ra.GetHistory(), m => m.Parts.ReplyTo == original.Key);

        // A direct chat: a reply to one of the other person's messages.
        var dmBob = new List<ChatMessage>();
        var chat = await alice.OpenDirectChatAsync(bob.Id);
        var chatBack = await bob.OpenDirectChatAsync(alice.Id);
        var dmAlice = new List<ChatMessage>();
        chat.MessageReceived += m => { lock (dmAlice) dmAlice.Add(m); };
        chatBack.MessageReceived += m => { lock (dmBob) dmBob.Add(m); };
        await chat.SendAsync("psst");
        await TestUtil.WaitFor(() => { lock (dmBob) return dmBob.Count >= 1; });
        ChatMessage psst; lock (dmBob) psst = dmBob.First(m => m.Body == "psst");
        await chatBack.SendAsync("yes?", psst.Key);
        await TestUtil.WaitFor(() => { lock (dmAlice) return dmAlice.Any(m => m.Body == "yes?"); });
        ChatMessage answer; lock (dmAlice) answer = dmAlice.First(m => m.Body == "yes?");
        Assert.Equal(psst.Key, answer.Parts.ReplyTo);
    }
}
