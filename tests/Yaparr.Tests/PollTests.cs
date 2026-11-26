using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

// Polls: a question in a message, answered by reacting with the number of an answer.
public class PollTests
{
    [Fact]
    public void A_poll_is_cleaned_and_keeps_what_is_left()
    {
        var poll = Poll.Clean("  Where\nto eat? ", new[] { " Pizza ", "", null, "Tacos" }, multiple: true)!;
        Assert.Equal("Where to eat?", poll.Question);
        Assert.Equal(new[] { "Pizza", "Tacos" }, poll.Options);
        Assert.True(poll.Multiple);
    }

    [Theory]
    [InlineData("", "a", "b")]
    [InlineData("Q", "a", "")]
    [InlineData("Q", "", "")]
    public void A_poll_without_a_question_or_two_answers_is_none(string q, string a, string b) => Assert.Null(Poll.Clean(q, new[] { a, b }, false));

    [Fact]
    public void The_limits_are_kept()
    {
        var poll = Poll.Clean(new string('q', 500), Enumerable.Range(0, 30).Select(i => new string('a', 300)), false)!;
        Assert.Equal(Poll.MaxQuestionLength, poll.Question.Length);
        Assert.Equal(Poll.MaxOptions, poll.Options.Count);
        Assert.All(poll.Options, o => Assert.Equal(Poll.MaxOptionLength, o.Length));
    }

    [Fact]
    public void Every_answer_has_an_emoji_that_can_be_a_reaction()
    {
        Assert.Equal(Poll.MaxOptions, Poll.Keycaps.Distinct().Count());
        Assert.All(Poll.Keycaps, k => Assert.True(EmojiData.TryNormalize(k, out var n) && n == k, k));
    }

    [Fact]
    public void A_poll_travels_in_the_message_and_its_words_are_the_fallback()
    {
        var poll = Poll.Clean("Lunch?", new[] { "Soup", "Salad", "Both" }, true)!;
        var text = MessageEnvelope.Wrap("ignored", null, null, poll: poll);
        var parts = MessageEnvelope.Parse(text);
        Assert.Equal(poll.Question, parts.Poll!.Question);
        Assert.Equal(poll.Options, parts.Poll.Options);
        Assert.True(parts.Poll.Multiple);
        Assert.Equal(poll.Words, parts.Body);
        Assert.Contains("Soup", parts.Body);
        Assert.Null(MessageEnvelope.Parse("just words").Poll);
    }

    [Fact]
    public void A_poll_that_does_not_hold_is_only_words()
    {
        // one answer: made by hand, as a bad sender could
        var text = "\u0001yaparr-env1\u0001{\"l\":{\"q\":\"Q?\",\"o\":[\"only\"]}}\u0001Q?";
        var parts = MessageEnvelope.Parse(text);
        Assert.Null(parts.Poll);
        Assert.Equal("Q?", parts.Body);
    }

    private static ReactionSummary Votes(int answer, params PeerId[] who) => new(Poll.Keycaps[answer], who);
    private static PeerId Person(char c) => new(new string(c, 64));

    [Fact]
    public void Votes_are_counted_by_answer()
    {
        var poll = Poll.Clean("Q", new[] { "a", "b", "c" }, false)!;
        var result = poll.Tally(new[] { Votes(0, Person('1'), Person('2')), Votes(2, Person('3')), new ReactionSummary("👍", new[] { Person('4') }) });
        Assert.Equal(new[] { 2, 0, 1 }, result.Options.Select(o => o.Count));
        Assert.Equal(3, result.Voters);   // the thumb is not a vote
    }

    [Fact]
    public void With_one_choice_a_person_counts_once_for_the_lowest_answer()
    {
        var poll = Poll.Clean("Q", new[] { "a", "b", "c" }, false)!;
        var result = poll.Tally(new[] { Votes(2, Person('1')), Votes(1, Person('1')) });
        Assert.Equal(new[] { 0, 1, 0 }, result.Options.Select(o => o.Count));
        Assert.Equal(1, result.Voters);
    }

    [Fact]
    public void With_many_choices_each_counts()
    {
        var poll = Poll.Clean("Q", new[] { "a", "b", "c" }, true)!;
        var result = poll.Tally(new[] { Votes(2, Person('1')), Votes(1, Person('1')) });
        Assert.Equal(new[] { 0, 1, 1 }, result.Options.Select(o => o.Count));
        Assert.Equal(1, result.Voters);
    }

    [Fact]
    public async Task A_vote_reaches_the_asker_and_changing_it_moves_it()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        using var b = TestUtil.NewNode(hub, "bob");
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Any(m => m.Id == b.Id) && rb.Members.Any(m => m.Id == a.Id), 30_000);

        var poll = Poll.Clean("Lunch?", new[] { "Soup", "Salad" }, false)!;
        var received = new TaskCompletionSource<ChatMessage>();
        rb.MessageReceived += m => received.TrySetResult(m);
        await ra.SendAsync(MessageEnvelope.Wrap("", null, null, poll: poll));
        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(message.Parts.Poll);

        await rb.SendReactionAsync(message.Key, poll.KeycapOf(1), on: true);
        await TestUtil.WaitFor(() => message.Parts.Poll!.Tally(ra.GetReactions().For(message.Key)).Options[1].Count == 1, 30_000);

        await rb.SendReactionAsync(message.Key, poll.KeycapOf(1), on: false);
        await rb.SendReactionAsync(message.Key, poll.KeycapOf(0), on: true);
        await TestUtil.WaitFor(() => message.Parts.Poll!.Tally(ra.GetReactions().For(message.Key)).Options[0].Count == 1, 30_000);
        Assert.Equal(0, poll.Tally(ra.GetReactions().For(message.Key)).Options[1].Count);
    }
}
