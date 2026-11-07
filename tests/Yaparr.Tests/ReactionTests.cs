using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class EmojiDataTests
{
    [Fact]
    public void The_quick_reactions_are_thirteen_emoji_of_the_set_in_their_standard_form()
    {
        Assert.Equal(13, EmojiData.Quick.Count);
        Assert.Equal(13, EmojiData.Quick.Distinct().Count());
        foreach (var emoji in EmojiData.Quick)
        {
            Assert.True(EmojiData.TryNormalize(emoji, out var standard), emoji);
            Assert.Equal(emoji, standard);
            Assert.NotNull(EmojiData.Find(emoji));
        }
        Assert.Equal("thumbs up", EmojiData.Find("\U0001F44D")!.Name);
        Assert.Equal("hundred points", EmojiData.Find("\U0001F4AF")!.Name);
        Assert.Equal("red heart", EmojiData.Find("❤️")!.Name);
    }

    [Fact]
    public void The_set_has_the_emoji_up_to_the_chosen_version_in_groups_and_nothing_newer()
    {
        Assert.InRange(EmojiData.Count, 1300, 2200);
        Assert.Equal(9, EmojiData.Groups.Count);
        Assert.Contains(EmojiData.Groups, g => g.Name == "Smileys & Emotion");
        Assert.Contains(EmojiData.Groups, g => g.Name == "Flags");
        // Emoji 14 and newer are held back: the melting face (14.0) and the shaking face (15.0).
        Assert.False(EmojiData.TryNormalize("\U0001FAE0", out _));
        Assert.False(EmojiData.TryNormalize("\U0001FAE8", out _));
        // The face exhaling (13.1) is in.
        Assert.True(EmojiData.TryNormalize("\U0001F62E‍\U0001F4A8", out _));
    }

    [Fact]
    public void An_emoji_is_accepted_however_its_selector_is_written_and_with_skin_tones_but_text_is_not()
    {
        // The red heart without its variation selector is the same emoji, and comes out in the standard form.
        Assert.True(EmojiData.TryNormalize("❤", out var heart));
        Assert.Equal("❤️", heart);
        // A skin tone on an emoji that has them.
        Assert.True(EmojiData.TryNormalize("\U0001F44D\U0001F3FD", out var tone));
        Assert.Equal("\U0001F44D\U0001F3FD", tone);
        // A flag and a family (joined emoji).
        Assert.True(EmojiData.TryNormalize("\U0001F1E9\U0001F1EA", out _));
        Assert.True(EmojiData.TryNormalize("\U0001F468‍\U0001F469‍\U0001F467", out _));

        Assert.False(EmojiData.TryNormalize(null, out _));
        Assert.False(EmojiData.TryNormalize("", out _));
        Assert.False(EmojiData.TryNormalize("a", out _));
        Assert.False(EmojiData.TryNormalize("thumbs up", out _));
        Assert.False(EmojiData.TryNormalize("\U0001F44D\U0001F44D", out _));         // two of them
        Assert.False(EmojiData.TryNormalize("\U0001F44D<script>", out _));
        Assert.False(EmojiData.TryNormalize(new string('‍', 100), out _));
    }

    [Theory]
    [InlineData("thumbs up", "\U0001F44D")]
    [InlineData("thumb", "\U0001F44D")]
    [InlineData("party", "\U0001F389")]
    [InlineData("fire", "\U0001F525")]
    [InlineData("100", "\U0001F4AF")]
    [InlineData("cry", "\U0001F622")]
    [InlineData("rofl", "\U0001F923")]
    public void Searching_by_name_or_keyword_finds_the_emoji_and_the_best_match_comes_first(string query, string expected)
    {
        var found = EmojiData.Search(query);
        Assert.NotEmpty(found);
        Assert.Contains(found, e => e.Emoji == expected);
        if (query is "thumbs up" or "fire") Assert.Equal(expected, found[0].Emoji);
    }

    [Fact]
    public void Several_words_all_have_to_match_and_nothing_found_is_an_empty_list()
    {
        var found = EmojiData.Search("red heart");
        Assert.Contains(found, e => e.Name == "red heart");
        Assert.All(found, e => Assert.True(e.LowerName.Contains("heart") || e.LowerKeywords.Any(k => k.StartsWith("heart"))));
        Assert.Empty(EmojiData.Search("qqqqzzzz"));
        Assert.Empty(EmojiData.Search(""));
        Assert.Empty(EmojiData.Search("   "));
        Assert.True(EmojiData.Search("face", 5).Count == 5);
    }
}

public class ReactionMessageTests
{
    private static readonly string Key = new string('a', 32);
    private static readonly string Thumbs = "\U0001F44D";

    [Fact]
    public void A_reaction_is_a_message_text_that_says_what_it_is_on_and_is_told_cheaply()
    {
        var text = MessageEnvelope.WrapReaction(Key, Thumbs, on: true);
        Assert.True(MessageEnvelope.IsReactionText(text));
        Assert.True(MessageEnvelope.TryReadReaction(text, out var on));
        Assert.Equal(new ReactionEvent(Key, Thumbs, true), on);

        var off = MessageEnvelope.WrapReaction(Key.ToUpperInvariant(), "❤", on: false);
        Assert.True(MessageEnvelope.TryReadReaction(off, out var taken));
        Assert.Equal(new ReactionEvent(Key, "❤️", false), taken);

        // Versions that do not know it show these words.
        Assert.EndsWith("reacted " + Thumbs, text);
        Assert.EndsWith("took back ❤️", off);
        Assert.Equal(Thumbs, MessageEnvelope.Parse(text).Reaction!.Emoji);
    }

    [Fact]
    public void Ordinary_messages_are_not_reactions_and_bad_ones_cannot_be_made_or_read()
    {
        Assert.False(MessageEnvelope.IsReactionText("hello"));
        Assert.False(MessageEnvelope.IsReactionText(MessageEnvelope.Wrap("hi", Key, null)));
        Assert.False(MessageEnvelope.TryReadReaction("hello", out _));
        Assert.Throws<ArgumentException>(() => MessageEnvelope.WrapReaction("not a key", Thumbs, true));
        Assert.Throws<ArgumentException>(() => MessageEnvelope.WrapReaction(Key, "thumbs up", true));

        // One that was built by hand with a text instead of an emoji, or a bad key, is not a reaction when read.
        var marker = MessageEnvelope.WrapReaction(Key, Thumbs, true);
        // (The header holds the emoji as JSON escapes.)
        const string Escaped = @"\uD83D\uDC4D";
        Assert.Contains(Escaped, marker);
        Assert.False(MessageEnvelope.TryReadReaction(marker.Replace(Escaped, "lol"), out _));
        Assert.False(MessageEnvelope.TryReadReaction(marker.Replace(Key, "zz"), out _));
    }

    [Fact]
    public void A_reply_and_a_reaction_do_not_get_in_each_others_way()
    {
        var reply = MessageEnvelope.Parse(MessageEnvelope.Wrap("agreed", Key, null));
        Assert.Equal(Key, reply.ReplyTo);
        Assert.Null(reply.Reaction);
    }
}

public class ReactionTallyTests
{
    private static readonly string A = new string('1', 64), B = new string('2', 64), A2 = new string('3', 64);
    private static readonly string Target = new string('a', 32);
    private static readonly string Thumbs = "\U0001F44D", Heart = "❤️";

    private static (string Key, string From, long Ts, string Text) Event(int n, string from, long ts, string emoji, bool on, string? target = null) =>
        (n.ToString("x32"), from, ts, MessageEnvelope.WrapReaction(target ?? Target, emoji, on));

    private static ReactionSet Tally(IEnumerable<(string, string, long, string)> events, Func<PeerId, bool>? skip = null) =>
        ReactionTally.Build(events, from => new PeerId(from == A2 ? A : from), skip ?? (_ => false));

    [Fact]
    public void Each_person_counts_once_and_the_emoji_come_in_the_order_they_were_first_used()
    {
        var set = Tally(new[] { Event(1, A, 10, Heart, true), Event(2, B, 20, Thumbs, true), Event(3, B, 30, Heart, true), Event(4, A, 40, Thumbs, true) });
        var reactions = set.For(Target);
        Assert.Equal(new[] { Heart, Thumbs }, reactions.Select(r => r.Emoji));
        Assert.Equal(new[] { 2, 2 }, reactions.Select(r => r.Count));
        Assert.True(set.Has(Target, Heart, new PeerId(A)));
        Assert.False(set.Has(Target, "\U0001F525", new PeerId(A)));
        Assert.Empty(set.For(new string('b', 32)));
        Assert.Equal(new[] { Target }, set.Targets);
    }

    [Fact]
    public void Taking_it_off_removes_it_and_the_latest_event_wins_in_any_order()
    {
        var events = new[] { Event(1, A, 10, Thumbs, true), Event(2, A, 20, Thumbs, false), Event(3, B, 15, Thumbs, true) };
        Assert.Equal(new[] { new PeerId(B) }, Tally(events).For(Target).Single().Accounts);
        // The same events in another order give the same answer.
        Assert.Equal(new[] { new PeerId(B) }, Tally(events.Reverse()).For(Target).Single().Accounts);
        // Put on again after being taken off.
        var again = events.Append(Event(4, A, 30, Thumbs, true));
        Assert.Equal(2, Tally(again).For(Target).Single().Count);
        // Nothing left: the message has no reactions at all.
        Assert.Empty(Tally(new[] { Event(1, A, 10, Thumbs, true), Event(2, A, 20, Thumbs, false) }).For(Target));
    }

    [Fact]
    public void Two_events_at_the_same_moment_are_decided_by_their_keys_so_every_device_agrees()
    {
        var on = Event(1, A, 10, Thumbs, true);
        var off = Event(2, A, 10, Thumbs, false);
        Assert.Empty(Tally(new[] { on, off }).For(Target));
        Assert.Empty(Tally(new[] { off, on }).For(Target));
    }

    [Fact]
    public void A_person_with_two_devices_counts_once_and_people_who_are_skipped_do_not_count()
    {
        var set = Tally(new[] { Event(1, A, 10, Thumbs, true), Event(2, A2, 20, Thumbs, true), Event(3, B, 30, Thumbs, true) });
        Assert.Equal(2, set.For(Target).Single().Count);
        var without = Tally(new[] { Event(1, A, 10, Thumbs, true), Event(3, B, 30, Thumbs, true) }, skip: who => who.Value == B);
        Assert.Equal(new[] { new PeerId(A) }, without.For(Target).Single().Accounts);
    }

    [Fact]
    public void Messages_that_are_not_good_reactions_are_left_out()
    {
        var bad = (n: 9.ToString("x32"), A, 5L, "just words");
        var set = Tally(new[] { bad, Event(1, B, 10, Thumbs, true) });
        Assert.Equal(1, set.For(Target).Single().Count);
    }
}

public class ReactionNodeTests
{
    private static readonly string Thumbs = "\U0001F44D", Heart = "❤️";

    [Fact]
    public async Task A_reaction_in_a_room_reaches_everyone_is_counted_but_is_not_a_message()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var bob = TestUtil.NewNode(hub, "bob");
        var ra = await alice.JoinRoomAsync("lobby");
        var rb = await bob.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => ra.Members.Count == 1 && rb.Members.Count == 1);

        var shownToBob = new List<ChatMessage>();
        rb.MessageReceived += shownToBob.Add;
        var changed = new List<string>();
        ra.ReactionsChanged += changed.Add;

        await ra.SendAsync("who wants pizza");
        await TestUtil.WaitFor(() => shownToBob.Count == 1);
        var key = shownToBob[0].Key;

        await rb.SendReactionAsync(key, Thumbs, on: true);
        await TestUtil.WaitFor(() => changed.Contains(key));
        Assert.Equal(new[] { bob.AccountId }, ra.GetReactions().For(key).Single().Accounts);
        Assert.Equal(Thumbs, ra.GetReactions().For(key).Single().Emoji);

        // Alice puts one on too; bob sees both; nobody is shown a message for any of it.
        await ra.SendReactionAsync(key, Heart, on: true);
        await TestUtil.WaitFor(() => rb.GetReactions().For(key).Count == 2);
        Assert.Single(shownToBob);
        Assert.Single(ra.GetHistory());
        Assert.Single(rb.GetHistory());

        // Taking one off.
        await rb.SendReactionAsync(key, Thumbs, on: false);
        await TestUtil.WaitFor(() => ra.GetReactions().For(key).Count == 1);
        Assert.Equal(Heart, ra.GetReactions().For(key).Single().Emoji);
        Assert.Equal(Heart, rb.GetReactions().For(key).Single().Emoji);
    }

    [Fact]
    public async Task A_reaction_that_is_not_an_emoji_cannot_be_sent_and_one_for_a_bad_key_cannot_either()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        var ra = await alice.JoinRoomAsync("lobby");
        await Assert.ThrowsAsync<ArgumentException>(() => ra.SendReactionAsync(new string('a', 32), "yes", true));
        await Assert.ThrowsAsync<ArgumentException>(() => ra.SendReactionAsync("nope", Thumbs, true));
        Assert.Empty(ra.GetHistory());
    }

    [Fact]
    public async Task A_reaction_in_a_direct_chat_is_counted_on_both_sides()
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

        var changed = new List<string>();
        toBob.ReactionsChanged += changed.Add;
        await toAlice.SendReactionAsync(key, Heart, true);
        await TestUtil.WaitFor(() => toBob.GetReactions().For(key).Count == 1 && changed.Contains(key));
        Assert.Equal(bob.AccountId, toBob.GetReactions().For(key).Single().Accounts.Single());
        Assert.Equal(1, toAlice.GetReactions().For(key).Single().Count);
        // Not a message in the history of either.
        Assert.Single(toBob.History);
        Assert.Single(toAlice.History);
        Assert.Single(shown);

        await toBob.SendReactionAsync(key, Heart, true);
        await TestUtil.WaitFor(() => toAlice.GetReactions().For(key).Single().Count == 2);
    }

    [Fact]
    public async Task Many_reactions_do_not_push_messages_out_of_the_history()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        var ra = await alice.JoinRoomAsync("lobby");
        await ra.SendAsync("the message");
        var key = ra.GetHistory().Single().Key;
        // Far more reactions than the history holds messages.
        for (int i = 0; i < 700; i++) await ra.SendReactionAsync(key, i % 2 == 0 ? Thumbs : Heart, on: i % 4 < 2);
        Assert.Equal("the message", ra.GetHistory().Single().Body);
    }
}
