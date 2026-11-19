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
    public async Task A_spinner_is_the_first_row_while_a_page_is_read_and_is_gone_when_it_is_there()
    {
        var conv = Room();
        for (int i = 50; i < 60; i++) conv.Add(Message(i, channel: "general"));
        conv.SelectChannel("general");
        var release = new TaskCompletionSource();
        conv.EarlierSource = async (_, _, _) => { await release.Task; return (Enumerable.Range(40, 10).Select(i => Message(i, channel: "general")).ToList(), true); };

        var loading = conv.LoadEarlierAsync();
        Assert.Equal(ConversationVm.EarlierState.Loading, conv.Earlier);
        Assert.True(conv.Messages[0].IsLoadingMarker);   // a row of the list, so it scrolls with the messages
        Assert.Equal(10, conv.ShownMessages);             // and is no message
        release.SetResult();
        Assert.Equal(10, await loading);

        Assert.Equal(ConversationVm.EarlierState.Idle, conv.Earlier);
        Assert.DoesNotContain(conv.Messages, m => m.IsMarker);
        Assert.Equal("message 40", conv.Messages[0].Text);
        Assert.Equal(20, conv.Messages.Count);
    }

    [Fact]
    public async Task When_there_is_nothing_earlier_and_nobody_to_ask_the_first_row_says_end_of_messages_until_another_channel_is_chosen()
    {
        var conv = Room();
        for (int i = 50; i < 60; i++) conv.Add(Message(i, channel: "general"));
        conv.SelectChannel("general");
        conv.EarlierSource = (_, _, _) => Task.FromResult<(IReadOnlyList<MessageVm>, bool)>((new List<MessageVm>(), false));

        Assert.Equal(0, await conv.LoadEarlierAsync());
        Assert.Equal(ConversationVm.EarlierState.End, conv.Earlier);
        Assert.True(conv.Messages[0].IsEndMarker);
        Assert.Equal(10, conv.ShownMessages);
        Assert.False(conv.HasEarlier);
        Assert.Equal(0, await conv.LoadEarlierAsync());   // nothing more is read, and the row stays

        conv.SelectChannel("general");   // the list is made again
        Assert.Equal(ConversationVm.EarlierState.Idle, conv.Earlier);
        Assert.DoesNotContain(conv.Messages, m => m.IsMarker);
    }

    [Fact]
    public async Task When_the_rooms_start_is_held_the_end_is_for_sure_so_nobody_is_asked_and_it_is_not_tried_again()
    {
        var conv = Room();
        for (int i = 50; i < 60; i++) conv.Add(Message(i, channel: "general"));
        conv.SelectChannel("general");
        // The last page held something, and nothing is earlier: with the start record held that is the beginning.
        conv.EarlierSource = (_, _, _) => Task.FromResult<(IReadOnlyList<MessageVm>, bool)>((Enumerable.Range(40, 10).Select(i => Message(i, channel: "general")).ToList(), false));
        conv.StartIsHeld = () => true;
        int asked = 0;
        conv.AskOthersForEarlier = (_, _, _) => { asked++; return 3; };

        Assert.Equal(10, await conv.LoadEarlierAsync());
        Assert.Equal(0, asked);
        Assert.Equal(ConversationVm.EarlierState.End, conv.Earlier);   // shown at once, not after a wait
        Assert.True(conv.Messages[0].IsEndMarker);
        Assert.Equal("message 40", conv.Messages[1].Text);

        // Not for a minute: for good (in this channel).
        Assert.False(conv.HasEarlier);
        conv.NoteEarlierArrived();   // what the others send makes no difference to a beginning that is known
        Assert.False(conv.HasEarlier);
        Assert.Equal(0, await conv.LoadEarlierAsync());
        Assert.Equal(0, asked);

        // Without the record it is the old way: the others are asked, and the spinner waits for them.
        var guess = Room();
        for (int i = 50; i < 60; i++) guess.Add(Message(i, channel: "general"));
        guess.SelectChannel("general");
        guess.EarlierSource = (_, _, _) => Task.FromResult<(IReadOnlyList<MessageVm>, bool)>((new List<MessageVm>(), false));
        guess.StartIsHeld = () => false;
        int guessAsked = 0;
        guess.AskOthersForEarlier = (_, _, _) => { guessAsked++; return 1; };
        await guess.LoadEarlierAsync();
        Assert.Equal(1, guessAsked);
        Assert.Equal(ConversationVm.EarlierState.Loading, guess.Earlier);
    }

    [Fact]
    public async Task While_the_others_are_asked_the_spinner_stays_and_it_becomes_the_end_when_they_say_nothing()
    {
        int before = ConversationVm.WaitForOthersMs;
        var post = ConversationVm.PostToInterface;
        ConversationVm.WaitForOthersMs = 150;
        ConversationVm.PostToInterface = action => action();   // no interface here: what would go to its thread runs where it is
        try
        {
            var conv = Room();
            for (int i = 50; i < 60; i++) conv.Add(Message(i, channel: "general"));
            conv.SelectChannel("general");
            conv.EarlierSource = (_, _, _) => Task.FromResult<(IReadOnlyList<MessageVm>, bool)>((new List<MessageVm>(), false));
            int asked = 0;
            conv.AskOthersForEarlier = (_, _, _) => { asked++; return 2; };

            Assert.Equal(0, await conv.LoadEarlierAsync());
            Assert.Equal(1, asked);
            Assert.Equal(ConversationVm.EarlierState.Loading, conv.Earlier);
            Assert.True(conv.Messages[0].IsLoadingMarker);

            // Nobody answers: the row says there is no more.
            await TestUtil.WaitFor(() => conv.Earlier == ConversationVm.EarlierState.End, 10_000);
            Assert.True(conv.Messages[0].IsEndMarker);
        }
        finally { ConversationVm.WaitForOthersMs = before; ConversationVm.PostToInterface = post; }
    }

    [Fact]
    public async Task What_the_others_send_while_the_spinner_waits_ends_the_wait_and_reading_goes_on()
    {
        var conv = Room();
        for (int i = 50; i < 60; i++) conv.Add(Message(i, channel: "general"));
        conv.SelectChannel("general");
        conv.EarlierSource = (_, _, _) => Task.FromResult<(IReadOnlyList<MessageVm>, bool)>((new List<MessageVm>(), false));
        conv.AskOthersForEarlier = (_, _, _) => 1;
        await conv.LoadEarlierAsync();
        Assert.Equal(ConversationVm.EarlierState.Loading, conv.Earlier);

        conv.NoteEarlierArrived();
        Assert.Equal(ConversationVm.EarlierState.Idle, conv.Earlier);
        Assert.DoesNotContain(conv.Messages, m => m.IsMarker);
        Assert.True(conv.HasEarlier);
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
