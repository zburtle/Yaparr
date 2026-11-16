using System.Buffers.Binary;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// Custom emoji: pictures written as :name: in the words, carried by the message that uses them and kept for a room by any member.
public class CustomEmojiTests
{
    // A file that is a PNG as far as its header goes (all the library looks at), made different by its width.
    private static byte[] Png(int width, int height = 20, int padding = 0)
    {
        var b = new byte[40 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)height);
        return b;
    }

    private static CustomEmoji Emoji(string name, byte[]? file = null)
    {
        file ??= Png(32);
        return new CustomEmoji(name, Attachment.HashOf(file), file.Length, "image/png", 32, 20);
    }

    // ---- the rules ----

    [Theory]
    [InlineData("party", "party")]
    [InlineData("  Party_Parrot ", "party_parrot")]
    [InlineData(":party:", "party")]
    [InlineData("ab", "ab")]
    public void A_name_is_cleaned_and_accepted(string typed, string name) => Assert.Equal(name, CustomEmojiRules.CleanName(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("has space")]
    [InlineData("dash-ed")]
    [InlineData("émoji")]
    [InlineData("this_name_is_far_too_long_to_use_ok")]
    public void A_name_that_cannot_be_used_is_refused(string? typed) => Assert.Null(CustomEmojiRules.CleanName(typed));

    [Fact]
    public void The_names_a_text_writes_are_found_once_each_and_numbers_with_colons_are_not_names()
    {
        Assert.Equal(new[] { "party", "cat_2" }, CustomEmojiRules.NamesIn("hi :party: and :cat_2: and :party: again"));
        Assert.Empty(CustomEmojiRules.NamesIn("the time is 12:30:45 today"));
        Assert.Empty(CustomEmojiRules.NamesIn("http://example.com:80:90/x"));
        Assert.Equal(new[] { "ok" }, CustomEmojiRules.NamesIn(":ok:"));
        Assert.Empty(CustomEmojiRules.NamesIn(":Party:"));          // names are lower case
        Assert.Empty(CustomEmojiRules.NamesIn(null));
    }

    [Fact]
    public void A_text_is_cut_at_the_names_it_writes()
    {
        var pieces = CustomEmojiRules.Pieces("so :party: and :cat: now");
        Assert.Equal(new (string, string?)[] { ("so ", null), (":party:", "party"), (" and ", null), (":cat:", "cat"), (" now", null) }, pieces);
        Assert.Equal(new (string, string?)[] { ("12:30:45", null) }, CustomEmojiRules.Pieces("12:30:45"));
        Assert.Equal(new (string, string?)[] { (":ok:", "ok") }, CustomEmojiRules.Pieces(":ok:"));
        Assert.Empty(CustomEmojiRules.Pieces(""));
        Assert.Empty(CustomEmojiRules.Pieces(null));
    }

    [Fact]
    public void A_picture_for_an_emoji_must_be_small()
    {
        Assert.True(CustomEmojiRules.TryRead(Png(128, 128), out var info, out _));
        Assert.Equal(128, info.Width);
        Assert.False(CustomEmojiRules.TryRead(Png(257, 20), out _, out var wide));
        Assert.Contains("257x20", wide);
        Assert.False(CustomEmojiRules.TryRead(Png(20, 300), out _, out _));
        Assert.False(CustomEmojiRules.TryRead(Png(32, 20, CustomEmojiRules.MaxBytes), out _, out var heavy));
        Assert.Contains("KB", heavy);
        Assert.False(CustomEmojiRules.TryRead("not a picture"u8.ToArray(), out _, out _));
        Assert.False(CustomEmojiRules.TryRead(Array.Empty<byte>(), out _, out _));
    }

    [Fact]
    public void A_message_of_only_custom_and_standard_emoji_is_only_emoji()
    {
        var known = new[] { "party", "cat" };
        Assert.True(CustomEmojiRules.IsOnlyEmoji(":party:", known));
        Assert.True(CustomEmojiRules.IsOnlyEmoji(":party: :cat:", known));
        Assert.True(CustomEmojiRules.IsOnlyEmoji(":party: \U0001F600", known));
        Assert.False(CustomEmojiRules.IsOnlyEmoji(":party: hello", known));
        Assert.False(CustomEmojiRules.IsOnlyEmoji(":unknown:", known));
        Assert.False(CustomEmojiRules.IsOnlyEmoji("\U0001F600", known));                  // no custom one: the standard rule applies
        Assert.False(CustomEmojiRules.IsOnlyEmoji(":party: :party: :party: :party:", known));
        Assert.False(CustomEmojiRules.IsOnlyEmoji(null, known));
    }

    // ---- in a message ----

    [Fact]
    public void A_message_carries_the_emoji_its_words_use_and_only_those()
    {
        var party = Emoji("party");
        var unused = Emoji("cat", Png(40));
        var text = MessageEnvelope.Wrap("so :party: today", null, null, emoji: new[] { party, unused });
        var parts = MessageEnvelope.Parse(text);
        Assert.Equal("so :party: today", parts.Body);
        Assert.Equal(party, Assert.Single(parts.Emoji!));
    }

    [Fact]
    public void A_message_that_uses_no_custom_emoji_is_unchanged()
    {
        Assert.Equal("hello :not_given:", MessageEnvelope.Wrap("hello :not_given:", null, null, emoji: new[] { Emoji("party") }));
        Assert.Equal("hi", MessageEnvelope.Wrap("hi", null, null));
        Assert.Null(MessageEnvelope.Parse("plain words").Emoji);
    }

    [Fact]
    public void Emoji_travel_with_a_reply_and_a_mention()
    {
        var key = new string('a', 32);
        var text = MessageEnvelope.Wrap(":party: :cat:", key, new[] { new Mention(new PeerId(new string('b', 64)), "Sam") }, emoji: new[] { Emoji("party"), Emoji("cat", Png(41)) });
        var parts = MessageEnvelope.Parse(text);
        Assert.Equal(key, parts.ReplyTo);
        Assert.Single(parts.Mentions);
        Assert.Equal(new[] { "party", "cat" }, parts.Emoji!.Select(e => e.Name));
    }

    [Fact]
    public void An_emoji_that_does_not_meet_the_limits_is_not_carried_or_read()
    {
        var big = new CustomEmoji("huge", new string('c', 64), 10, "image/png", 1000, 1000);
        var badHash = new CustomEmoji("odd", "nothex", 10, "image/png", 10, 10);
        var svg = new CustomEmoji("vector", new string('d', 64), 10, "image/svg+xml", 10, 10);
        Assert.Equal(":huge: :odd: :vector:", MessageEnvelope.Wrap(":huge: :odd: :vector:", null, null, emoji: new[] { big, badHash, svg }));

        // One that arrives in a header anyway (from a version that does not check) is left out when read.
        var text = MessageEnvelope.Wrap(":party:", null, null, emoji: new[] { Emoji("party") });
        var tampered = text.Replace("\"w\":32", "\"w\":9999");
        Assert.NotEqual(text, tampered);
        Assert.Null(MessageEnvelope.Parse(tampered).Emoji);
    }

    [Fact]
    public void A_header_may_not_name_emoji_the_words_do_not_use()
    {
        var text = MessageEnvelope.Wrap(":party:", null, null, emoji: new[] { Emoji("party") });
        // The words were changed afterwards: the picture is no longer used, so it is not read.
        var swapped = text.Replace("\u0001:party:", "\u0001:other:");
        Assert.NotEqual(text, swapped);
        Assert.Null(MessageEnvelope.Parse(swapped).Emoji);
    }

    [Fact]
    public void No_more_than_ten_emoji_are_carried_in_a_message()
    {
        var emoji = Enumerable.Range(0, 14).Select(i => Emoji($"e{i:00}", Png(30 + i))).ToArray();
        var words = string.Join(" ", emoji.Select(e => $":{e.Name}:"));
        var parts = MessageEnvelope.Parse(MessageEnvelope.Wrap(words, null, null, emoji: emoji));
        Assert.Equal(CustomEmojiRules.MaxPerMessage, parts.Emoji!.Count);
    }

    // ---- between people ----

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
    public async Task A_message_with_an_emoji_brings_its_picture_to_the_reader()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        var file = Png(48, 48, 300);
        var emoji = a.PrepareEmoji("Party", file);          // the name is cleaned
        Assert.Equal("party", emoji.Name);
        var received = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => received.TrySetResult(m);
        await ra.SendAsync(MessageEnvelope.Wrap("big news :party:", null, null, emoji: new[] { emoji }));
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(emoji, Assert.Single(message.Parts.Emoji!));
        await TestUtil.WaitFor(() => b.HasAttachment(emoji.Hash), 30_000);          // fetched by itself
        Assert.Equal(file, b.ReadAttachment(emoji.Hash));
    }

    [Fact]
    public void Preparing_an_emoji_refuses_what_cannot_be_one()
    {
        using var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alone");
        Assert.Throws<ArgumentException>(() => node.PrepareEmoji("x", Png(32)));
        Assert.Throws<ArgumentException>(() => node.PrepareEmoji("big_one", Png(300)));
        Assert.Throws<ArgumentException>(() => node.PrepareEmoji("text", "words"u8.ToArray()));
    }

    // ---- an emoji set for a room ----

    [Fact]
    public async Task An_emoji_one_member_adds_belongs_to_the_room_and_reaches_the_others_and_newcomers()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        var file = Png(64, 64, 100);
        var added = ra.AddEmoji(" Party ", file);
        Assert.Equal("party", added.Name);
        Assert.True(added.Available);
        await TestUtil.WaitFor(() => rb.Emoji.Count == 1, 20_000);
        var seen = rb.Emoji.Single();
        Assert.Equal("party", seen.Name);
        Assert.Equal(a.Id, seen.Owner);
        rb.RequestGifs();
        await TestUtil.WaitFor(() => rb.Emoji.Single().Available, 20_000);
        Assert.Equal(file, rb.GetGif(seen.Id));

        // Emoji are not pictures of the GIF shelf, and pictures of the shelf are not emoji.
        Assert.Empty(ra.Gifs);
        Assert.Empty(rb.Gifs);
        ra.AddGif(Png(77, 77));
        await TestUtil.WaitFor(() => rb.Gifs.Count == 1, 20_000);
        Assert.Single(rb.Emoji);

        using var c = TestUtil.NewNode(hub, "carol");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(ra.Invite), "lobby");
        await TestUtil.WaitFor(() => rc.Emoji.Count == 1 && rc.Gifs.Count == 1, 30_000);
    }

    [Fact]
    public async Task A_room_emoji_needs_a_free_name_and_a_picture_that_is_small()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);
        using var _ = a; using var __ = b;

        ra.AddEmoji("party", Png(32));
        Assert.Equal("party", ra.AddEmoji("party", Png(32)).Name);                 // the same picture under the same name: nothing new
        Assert.Single(ra.Emoji);
        var taken = Assert.Throws<ArgumentException>(() => ra.AddEmoji("party", Png(33)));
        Assert.Contains("already", taken.Message);
        var standard = Assert.Throws<ArgumentException>(() => ra.AddEmoji("thumbs_up", Png(34)));
        Assert.Contains("standard emoji", standard.Message);
        Assert.Throws<ArgumentException>(() => ra.AddEmoji("x", Png(35)));
        Assert.Throws<ArgumentException>(() => ra.AddEmoji("huge", Png(512)));
        Assert.Throws<ArgumentException>(() => ra.AddEmoji("heavy", Png(36, 20, CustomEmojiRules.MaxBytes)));
        Assert.Single(ra.Emoji);

        // The one who added it can take it away, and then the name is free again, for anyone.
        await TestUtil.WaitFor(() => rb.Emoji.Count == 1, 20_000);
        ra.RemoveGif(ra.Emoji.Single().Id);
        await TestUtil.WaitFor(() => rb.Emoji.Count == 0, 20_000);
        Assert.Equal("party", rb.AddEmoji("party", Png(37)).Name);
        await TestUtil.WaitFor(() => ra.Emoji.Count == 1 && ra.Emoji.Single().Owner == b.Id, 20_000);
    }

    [Fact]
    public async Task The_owner_can_take_down_any_room_emoji()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, ra, rb) = await Pair(hub);          // alice made the room, so she owns it
        using var _ = a; using var __ = b;

        rb.AddEmoji("rude", Png(38));
        await TestUtil.WaitFor(() => ra.Emoji.Count == 1, 20_000);
        ra.DeleteGif(ra.Emoji.Single().Id);
        await TestUtil.WaitFor(() => rb.Emoji.Count == 0, 20_000);
        Assert.Empty(ra.Emoji);
    }

    [Fact]
    public void An_emoji_statement_cannot_be_given_another_name_without_the_signature_failing()
    {
        using var me = PeerIdentity.Generate();
        const string room = "room-key";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = SignedGif.Create(me, room, SignedGif.NewId(), "me", Attachment.HashOf(Png(32)), 40, "image/png", 32, 20, now, deleted: false, emoji: "party");
        Assert.True(SignedGif.Verify(entry, room));
        Assert.False(SignedGif.Verify(entry with { Emoji = "other" }, room));              // renamed
        Assert.False(SignedGif.Verify(entry with { Emoji = null }, room));                 // passed off as a picture of the shelf
        Assert.False(SignedGif.Verify(entry with { Emoji = "Not Valid" }, room));

        var picture = SignedGif.Create(me, room, SignedGif.NewId(), "me", Attachment.HashOf(Png(32)), 40, "image/png", 32, 20, now, deleted: false);
        Assert.True(SignedGif.Verify(picture, room));
        Assert.False(SignedGif.Verify(picture with { Emoji = "party" }, room));            // a shelf picture made an emoji

        var large = SignedGif.Create(me, room, SignedGif.NewId(), "me", Attachment.HashOf(Png(300)), 40, "image/png", 300, 20, now, deleted: false, emoji: "wide");
        Assert.False(SignedGif.Verify(large, room));                                       // too big for an emoji, though fine for a picture
    }
}
