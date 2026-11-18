using System.Buffers.Binary;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// A custom emoji (a room's or a person's own) used as a reaction: it travels with the reaction like it does with a message, and counts like any emoji.
public class CustomReactionTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static byte[] Png(int width, int padding = 0)
    {
        var b = new byte[40 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 20);
        return b;
    }

    private static CustomEmoji Emoji(string name, byte[]? file = null)
    {
        file ??= Png(32);
        return new CustomEmoji(name, Attachment.HashOf(file), file.Length, "image/png", 32, 20);
    }

    [Fact]
    public void A_custom_emoji_reaction_carries_its_description_and_is_named_by_its_name_and_picture()
    {
        var party = Emoji("party");
        var text = MessageEnvelope.WrapReaction(Key, "", on: true, custom: party);
        Assert.True(MessageEnvelope.IsReactionText(text));
        Assert.Contains(":party:", text.Split('\u0001')[^1]);   // what an old version shows for it

        Assert.True(MessageEnvelope.TryReadReaction(text, out var on));
        Assert.Equal(new ReactionEvent(Key, ":party:" + party.Hash, true, null, null, party), on);

        Assert.True(MessageEnvelope.TryReadReaction(MessageEnvelope.WrapReaction(Key, "", on: false, custom: party), out var off));
        Assert.False(off.On);
        Assert.Equal(on.Emoji, off.Emoji);
    }

    [Fact]
    public void A_custom_emoji_that_does_not_meet_the_limits_is_not_a_reaction()
    {
        Assert.Throws<ArgumentException>(() => MessageEnvelope.WrapReaction(Key, "", true, custom: Emoji("party") with { Name = "x" }));
        Assert.Throws<ArgumentException>(() => MessageEnvelope.WrapReaction(Key, "", true, custom: Emoji("party") with { Size = 10_000_000 }));
        Assert.Throws<ArgumentException>(() => MessageEnvelope.WrapReaction("nope", "", true, custom: Emoji("party")));

        // One that arrives with a changed description (a name that is not one) is dropped by the reader.
        var good = MessageEnvelope.WrapReaction(Key, "", true, custom: Emoji("party"));
        Assert.False(MessageEnvelope.TryReadReaction(good.Replace("\"n\":\"party\"", "\"n\":\"a b\""), out _));
        Assert.False(MessageEnvelope.TryReadReaction(good.Replace("image/png", "text/html"), out _));
    }

    [Fact]
    public void Two_people_with_the_same_picture_count_together_and_a_different_picture_with_the_same_name_is_another_reaction()
    {
        var alice = PeerIdentity.Generate().Id;
        var bob = PeerIdentity.Generate().Id;
        var party = Emoji("party");
        var other = Emoji("party", Png(48, padding: 5));
        string Text(CustomEmoji e) => MessageEnvelope.WrapReaction(Key, "", true, custom: e);
        var messages = new[]
        {
            ("k1", alice.Value, 100L, Text(party)),
            ("k2", bob.Value, 110L, Text(party)),
            ("k3", bob.Value, 120L, Text(other)),
        };
        var set = ReactionTally.Build(messages, device => new PeerId(device), _ => false);
        var summaries = set.For(Key);
        Assert.Equal(2, summaries.Count);
        Assert.Equal((2, party), (summaries[0].Count, summaries[0].Custom));
        Assert.Equal((1, other), (summaries[1].Count, summaries[1].Custom));
        Assert.True(set.Has(Key, MessageEnvelope.CustomKey(party), alice));
        Assert.False(set.Has(Key, MessageEnvelope.CustomKey(other), alice));
    }

    [Fact]
    public async Task A_custom_emoji_put_on_a_message_reaches_the_room_with_its_picture()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        var ra = await alice.JoinRoomAsync("lobby");
        var rb = await bob.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);
        var shown = new List<ChatMessage>();
        rb.MessageReceived += shown.Add;
        var changed = new List<string>();
        ra.ReactionsChanged += changed.Add;

        await ra.SendAsync("who wants pizza");
        await TestUtil.WaitFor(() => shown.Count == 1);
        var key = shown[0].Key;

        var file = Png(32, padding: 7);
        var party = bob.PrepareEmoji("party", file);
        await rb.SendReactionAsync(key, party, on: true);
        await TestUtil.WaitFor(() => changed.Contains(key));

        var summary = ra.GetReactions().For(key).Single();
        Assert.Equal(party, summary.Custom);
        Assert.Equal(new[] { bob.AccountId }, summary.Accounts);
        // Alice has not got that picture: it is fetched, from bob, as the reaction arrives.
        await TestUtil.WaitFor(() => alice.HasAttachment(party.Hash));
        Assert.Equal(file, alice.ReadAttachment(party.Hash));
        Assert.Single(ra.GetHistory());   // a reaction is not shown as a message

        // Taking it off.
        await rb.SendReactionAsync(key, party, on: false);
        await TestUtil.WaitFor(() => ra.GetReactions().For(key).Count == 0);
    }

    [Fact]
    public async Task A_custom_emoji_reaction_in_a_direct_chat_is_counted_on_both_sides()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        var ra = await alice.JoinRoomAsync("lobby");
        var rb = await bob.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);
        var toBob = await alice.OpenDirectChatAsync(bob.Id);
        var toAlice = await bob.OpenDirectChatAsync(alice.Id);
        var shown = new List<ChatMessage>();
        toAlice.MessageReceived += shown.Add;
        await toBob.SendAsync("hello");
        await TestUtil.WaitFor(() => shown.Count == 1);
        var key = shown[0].Key;

        var party = bob.PrepareEmoji("party", Png(32, padding: 3));
        await toAlice.SendReactionAsync(key, party, on: true);
        await TestUtil.WaitFor(() => toBob.GetReactions().For(key).Count == 1);
        Assert.Equal(party, toBob.GetReactions().For(key).Single().Custom);
        Assert.Equal(party, toAlice.GetReactions().For(key).Single().Custom);
    }
}
