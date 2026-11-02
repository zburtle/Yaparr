using Yaparr.Chat;
using Yaparr.Desktop;
using Yaparr.Identity;

namespace Yaparr.Tests;

/// <summary>How the app shows replies and names: what a reply quotes, and which words stand out.</summary>
public class RepliesViewTests
{
    private static readonly PeerId Alice = new(new string('1', 64));

    private static ConversationVm Conversation() => new("room", isRoom: true, _ => Task.FromResult("id"));

    private static MessageVm Message(string key, string sender, string text, bool mine = false, string? replyTo = null, bool mentionsMe = false) =>
        new(DateTimeOffset.Now, sender, text, isMine: mine, id: key) { Key = key, ReplyKey = replyTo, MentionsMe = mentionsMe };

    private static readonly string K1 = new string('a', 32), K2 = new string('b', 32), K3 = new string('c', 32);

    [Fact]
    public void A_reply_quotes_the_message_it_answers_even_when_the_reply_is_shown_first()
    {
        var conv = Conversation();
        conv.Add(Message(K1, "Bob", "who has the notes?\nreally?"));
        var reply = Message(K2, "Alice", "I do", replyTo: K1);
        conv.Add(reply);

        Assert.True(reply.HasQuote);
        Assert.Equal("Bob", reply.QuoteSender);
        Assert.Equal("who has the notes? really?", reply.QuoteText);     // one line
        Assert.False(reply.ReplyToMe);

        // Catching up can bring the reply before the message: it quotes it once that arrives.
        var early = Message(K3, "Carol", "me too", replyTo: new string('d', 32));
        conv.Add(early);
        Assert.Equal("an earlier message that is not here", early.QuoteText);
        conv.Add(Message(new string('d', 32), "Dave", "the original"));
        Assert.Equal("Dave", early.QuoteSender);
        Assert.Equal("the original", early.QuoteText);
    }

    [Fact]
    public void An_answer_to_your_own_message_stands_out_but_an_answer_to_someone_else_does_not()
    {
        var conv = Conversation();
        conv.Add(Message(K1, "Me", "my question", mine: true));
        conv.Add(Message(K2, "Bob", "someone else's"));
        var toMe = Message(K3, "Bob", "answer", replyTo: K1);
        var toBob = Message(new string('e', 32), "Carol", "answer", replyTo: K2);
        var mine = Message(new string('f', 32), "Me", "follow-up", mine: true, replyTo: K1);
        conv.Add(toMe); conv.Add(toBob); conv.Add(mine);

        Assert.True(toMe.ReplyToMe);
        Assert.True(toMe.Highlight);
        Assert.False(toBob.Highlight);
        Assert.False(mine.Highlight);            // what you wrote yourself never stands out for you
    }

    [Fact]
    public void Being_named_stands_out_and_marks_the_conversation_until_it_is_read()
    {
        var named = Message(K1, "Bob", "@Alice look", mentionsMe: true);
        Assert.True(named.Highlight);
        Assert.False(Message(K2, "Bob", "@Alice look", mine: true, mentionsMe: true).Highlight);

        var conv = Conversation();
        conv.NoteUnread(null, mentioned: false);
        Assert.Equal("1", conv.UnreadText);
        conv.NoteUnread(null, mentioned: true);
        Assert.Equal("2 @", conv.UnreadText);
        Assert.True(conv.Mentioned);
        Assert.Contains("(2 @)", conv.Label);

        conv.Unread = 0;                           // read
        Assert.False(conv.Mentioned);
        Assert.Equal("room", conv.Label);
    }

    [Fact]
    public void The_words_are_cut_where_the_names_are_and_a_name_can_hold_a_space()
    {
        var marks = new[] { "@Alice A", "@bob" };
        Assert.Equal(new[] { ("hey ", false), ("@Alice A", true), (" and ", false), ("@BOB", true), (", hi", false) },
            MessageText.Split("hey @Alice A and @BOB, hi", marks).ToArray());
        Assert.Equal(new[] { ("no names here", false) }, MessageText.Split("no names here", marks).ToArray());
        Assert.Equal(new[] { ("plain", false) }, MessageText.Split("plain", null).ToArray());
        Assert.Equal(new[] { ("", false) }, MessageText.Split("", marks).ToArray());
        // The longer name wins where two start at the same place.
        Assert.Equal(new[] { ("@Alice A", true) }, MessageText.Split("@Alice A", new[] { "@Alice", "@Alice A" }).ToArray());
    }

    [Fact]
    public void A_picture_is_quoted_by_its_caption_or_as_a_picture()
    {
        var picture = new Attachment(new string('a', 64), "x.png", 100, "image/png", 10, 10);
        var plain = new MessageVm(DateTimeOffset.Now, "Bob", "", id: "p", attachment: picture);
        var captioned = new MessageVm(DateTimeOffset.Now, "Bob", "look at this", id: "q", attachment: picture);
        Assert.Equal("📷 Picture", plain.Preview);
        Assert.Equal("📷 look at this", captioned.Preview);
        Assert.Equal(141, new MessageVm(DateTimeOffset.Now, "Bob", new string('x', 500)).Preview.Length);   // 140 and an ellipsis
    }
}
