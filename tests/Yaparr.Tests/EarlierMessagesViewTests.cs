using Yaparr.Desktop;

namespace Yaparr.Tests;

/// <summary>Reading back through a conversation: a page of earlier messages goes above, once, until there are none.</summary>
public class EarlierMessagesViewTests
{
    private static string K(int i) => i.ToString("x").PadLeft(32, '0');

    private static MessageVm Message(int i, string? replyTo = null, string? channel = null) =>
        new(DateTimeOffset.UnixEpoch.AddMinutes(i), "Bob", $"message {i}", isMine: false, id: K(i)) { Key = K(i), ReplyKey = replyTo, Channel = channel };

    private static ConversationVm Room() => new("room", isRoom: true, _ => Task.FromResult("id"));

    [Fact]
    public async Task A_page_of_earlier_messages_goes_above_and_reading_stops_when_there_are_no_more()
    {
        var conv = Room();
        for (int i = 50; i < 60; i++) conv.Add(Message(i, channel: "general"));
        conv.SelectChannel("general");
        var asked = new List<(string? Channel, string Before)>();
        conv.EarlierSource = (channel, before, _) =>
        {
            asked.Add((channel, before));
            return Task.FromResult<(IReadOnlyList<MessageVm>, bool)>(before == K(50)
                ? (Enumerable.Range(30, 20).Select(i => Message(i, channel: "general")).ToList(), true)
                : (Enumerable.Range(0, 5).Select(i => Message(i, channel: "general")).ToList(), false));
        };

        Assert.True(conv.HasEarlier);
        Assert.Equal(20, await conv.LoadEarlierAsync());
        Assert.Equal(30, conv.Messages.Count);
        Assert.Equal("message 30", conv.Messages[0].Text);
        Assert.Equal("message 59", conv.Messages[^1].Text);
        Assert.Equal(("general", K(50)), asked[0]);

        // The next page starts from what is now the oldest message.
        Assert.Equal(5, await conv.LoadEarlierAsync());
        Assert.Equal(K(30), asked[1].Before);
        Assert.False(conv.HasEarlier);
        Assert.Equal(0, await conv.LoadEarlierAsync());
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task Only_one_page_is_read_at_a_time_and_a_chat_with_nothing_to_read_back_to_asks_for_nothing()
    {
        var conv = Room();
        conv.Add(Message(5, channel: "general"));
        conv.SelectChannel("general");
        Assert.False(conv.HasEarlier);
        Assert.Equal(0, await conv.LoadEarlierAsync());

        var release = new TaskCompletionSource();
        int calls = 0;
        conv.EarlierSource = async (_, _, _) => { calls++; await release.Task; return (new List<MessageVm> { Message(4, channel: "general") }, true); };
        var first = conv.LoadEarlierAsync();
        Assert.Equal(0, await conv.LoadEarlierAsync());   // a page is already on its way
        release.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_quote_that_could_not_be_followed_is_filled_in_when_the_message_it_answers_is_read()
    {
        var conv = Room();
        var reply = Message(60, replyTo: K(10), channel: "general");
        conv.Add(reply);
        conv.SelectChannel("general");
        Assert.Equal(ConversationVm.MissingQuoteText, reply.QuoteText);
        conv.EarlierSource = (_, _, _) => Task.FromResult<(IReadOnlyList<MessageVm>, bool)>((new List<MessageVm> { Message(10, channel: "general") }, false));

        await conv.LoadEarlierAsync();
        Assert.Equal("message 10", reply.QuoteText);
    }

    [Fact]
    public void A_reload_keeps_the_messages_read_from_earlier_history_that_are_still_to_be_shown_and_drops_the_rest()
    {
        var conv = Room();
        foreach (var i in new[] { 1, 2, 3, 50, 51 }) conv.Add(Message(i, channel: "general"));
        conv.SelectChannel("general");

        // The newest ones are 50 and 51; 1, 2 and 3 were read earlier, and the moderators have since taken 2 down.
        conv.Reload(new[] { Message(50, channel: "general"), Message(51, channel: "general") },
            keys => keys.Where(k => k != K(2)).ToHashSet());
        Assert.Equal(new[] { "message 1", "message 3", "message 50", "message 51" }, conv.Messages.Select(m => m.Text));

        // Without being told, only what the reload holds stays.
        conv.Reload(new[] { Message(50, channel: "general"), Message(51, channel: "general") });
        Assert.Equal(new[] { "message 50", "message 51" }, conv.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task When_this_device_has_nothing_earlier_the_others_are_asked_once_and_asked_again_only_after_they_sent_something()
    {
        var conv = Room();
        for (int i = 50; i < 53; i++) conv.Add(Message(i, channel: "general"));
        conv.SelectChannel("general");
        var local = new Queue<(IReadOnlyList<MessageVm>, bool)>();
        local.Enqueue((new List<MessageVm> { Message(49, channel: "general") }, false));
        conv.EarlierSource = (_, _, _) => Task.FromResult(local.Count > 0 ? local.Dequeue() : ((IReadOnlyList<MessageVm>)new List<MessageVm>(), false));
        var asked = new List<(string? Channel, string Key)>();
        conv.AskOthersForEarlier = (channel, key, _) => { lock (asked) asked.Add((channel, key)); return 2; };

        // The last of what this device holds is read: the others are asked for what is before it.
        Assert.Equal(1, await conv.LoadEarlierAsync());
        lock (asked) Assert.Equal(("general", K(49)), Assert.Single(asked));
        Assert.False(conv.HasEarlier);                      // not again for a minute, however the list is scrolled
        Assert.Equal(0, await conv.LoadEarlierAsync());
        lock (asked) Assert.Single(asked);

        // They sent something (it is kept; reading goes on) and, when that runs out too, they are asked again.
        conv.NoteEarlierArrived();
        Assert.True(conv.HasEarlier);
        local.Enqueue((new List<MessageVm> { Message(48, channel: "general") }, false));
        Assert.Equal(1, await conv.LoadEarlierAsync());
        await TestUtil.WaitFor(() => { lock (asked) return asked.Count == 2; }, 10_000);
        lock (asked) Assert.Equal(K(48), asked[1].Key);
    }

    [Fact]
    public async Task A_conversation_that_nobody_can_be_asked_about_just_stops_at_the_end()
    {
        var conv = Room();
        conv.Add(Message(5, channel: "general"));
        conv.SelectChannel("general");
        conv.EarlierSource = (_, _, _) => Task.FromResult<(IReadOnlyList<MessageVm>, bool)>((new List<MessageVm> { Message(4, channel: "general") }, false));
        await conv.LoadEarlierAsync();
        Assert.False(conv.HasEarlier);
    }
}
