using Yaparr.AppCore;

namespace Yaparr.Tests;

public class NotificationPolicyTests
{
    private static NotificationPolicy.Message Room(bool mine = false, bool mentions = false, bool reply = false, bool all = false, int seconds = 5) =>
        new(IsDirect: false, mine, mentions, reply, all, TimeSpan.FromSeconds(seconds));

    private static NotificationPolicy.Message Direct(bool mine = false, int seconds = 5) =>
        new(IsDirect: true, mine, MentionsMe: false, ReplyToMe: false, RoomNotifiesAll: false, TimeSpan.FromSeconds(seconds));

    private static bool Notify(NotificationPolicy.Message m, bool foreground = false, bool on = true) => NotificationPolicy.ShouldNotify(m, foreground, on);

    [Fact]
    public void Direct_messages_mentions_and_replies_notify_and_other_room_messages_do_not()
    {
        Assert.True(Notify(Direct()));
        Assert.True(Notify(Room(mentions: true)));
        Assert.True(Notify(Room(reply: true)));
        Assert.False(Notify(Room()));
    }

    [Fact]
    public void A_room_set_to_notify_for_everything_notifies_for_every_message()
    {
        Assert.True(Notify(Room(all: true)));
    }

    [Fact]
    public void Nothing_notifies_while_the_app_is_open_or_when_notifications_are_off_or_for_your_own_messages()
    {
        Assert.False(Notify(Direct(), foreground: true));
        Assert.False(Notify(Room(mentions: true, all: true), foreground: true));
        Assert.False(Notify(Direct(), on: false));
        Assert.False(Notify(Direct(mine: true)));
        Assert.False(Notify(Room(mine: true, mentions: true, all: true)));
    }

    [Fact]
    public void An_old_message_is_history_and_does_not_notify()
    {
        Assert.True(Notify(Direct(seconds: 100)));
        Assert.False(Notify(Direct(seconds: 600)));
        Assert.False(Notify(Room(mentions: true, seconds: 3600)));
    }

    [Theory]
    [InlineData("Alex", "the new room", false, "Alex in the new room")]
    [InlineData("Alex", "Alex", true, "Alex")]
    [InlineData("Alex", "Alex (direct)", true, "Alex")]
    public void The_lock_screen_says_who_and_where_but_not_the_words(string sender, string chat, bool direct, string expected) =>
        Assert.Equal(expected, NotificationPolicy.LockScreenText(sender, chat, direct));

    [Fact]
    public void The_lock_screen_line_is_one_short_line_whatever_the_names_hold()
    {
        var line = NotificationPolicy.LockScreenText("Al\nex\t" + new string('x', 200), "room\u0007" + new string('y', 200), isDirect: false);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\t', line);
        Assert.DoesNotContain('\u0007', line);
        Assert.True(line.Length <= 40 + 4 + 40);
    }

    [Fact]
    public void The_body_is_the_words_or_what_the_message_carries_and_is_cut_to_a_line()
    {
        Assert.Equal("hello there", NotificationPolicy.Body("  hello \n there ", hasPicture: false, isGif: false));
        Assert.Equal("Sent a picture", NotificationPolicy.Body("", hasPicture: true, isGif: false));
        Assert.Equal("Sent a GIF", NotificationPolicy.Body("  ", hasPicture: true, isGif: true));
        Assert.Equal("Picture: look at this", NotificationPolicy.Body("look at this", hasPicture: true, isGif: false));
        Assert.Equal("", NotificationPolicy.Body("", hasPicture: false, isGif: false));
        var long_ = NotificationPolicy.Body(new string('a', 500), false, false);
        Assert.True(long_.Length <= 140);
        Assert.EndsWith("…", long_);
    }
}
