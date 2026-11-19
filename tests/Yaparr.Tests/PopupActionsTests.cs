using Yaparr.Desktop;

namespace Yaparr.Tests;

// What the desktop tells about a pop-up (a click, the Reply button, a typed reply, Mute) reaches the chat the pop-up was for, and only that one.
public class PopupActionsTests
{
    private sealed class Calls
    {
        public int Opened, Muted;
        public readonly List<string> Replies = new();
        public PopupActions Actions(bool canMute = true) => new(() => Opened++, text => Replies.Add(text), canMute ? () => Muted++ : null);
    }

    [Fact]
    public void A_click_the_reply_button_and_anything_else_open_the_chat_and_mute_mutes_the_room()
    {
        var registry = new PopupRegistry();
        var chat = new Calls();
        registry.Add(7, chat.Actions());

        Assert.True(registry.Invoke(7, "default"));
        Assert.True(registry.Invoke(7, "reply"));          // where the desktop has no box to type in, Reply opens the chat to write there
        Assert.True(registry.Invoke(7, "inline-reply"));
        Assert.Equal(3, chat.Opened);
        Assert.True(registry.Invoke(7, "mute"));
        Assert.Equal(1, chat.Muted);
        Assert.Equal(3, chat.Opened);
    }

    [Fact]
    public void A_typed_reply_goes_to_the_chat_trimmed_and_an_empty_one_goes_nowhere()
    {
        var registry = new PopupRegistry();
        var chat = new Calls();
        registry.Add(1, chat.Actions());

        Assert.True(registry.Reply(1, "  on my way \n"));
        Assert.False(registry.Reply(1, "   "));
        Assert.False(registry.Reply(1, ""));
        Assert.Equal(new[] { "on my way" }, chat.Replies);
    }

    [Fact]
    public void An_answer_for_a_pop_up_that_is_gone_or_unknown_does_nothing_and_a_direct_chat_has_no_mute()
    {
        var registry = new PopupRegistry();
        var chat = new Calls();
        registry.Add(1, chat.Actions(canMute: false));
        registry.Add(2, chat.Actions());

        Assert.False(registry.Invoke(99, "default"));
        Assert.False(registry.Reply(99, "hello"));
        Assert.True(registry.Invoke(1, "mute"));   // nothing to mute: accepted, nothing happens
        Assert.Equal(0, chat.Muted);

        registry.Remove(2);
        Assert.False(registry.Invoke(2, "default"));
        Assert.False(registry.Reply(2, "too late"));
        Assert.Empty(chat.Replies);
    }

    [Fact]
    public void A_newer_pop_up_that_replaces_an_older_one_takes_its_place_and_only_so_many_are_kept()
    {
        var registry = new PopupRegistry();
        var first = new Calls();
        var second = new Calls();
        registry.Add(5, first.Actions());
        registry.Add(5, second.Actions());   // same number: the desktop replaced the pop-up
        registry.Invoke(5, "default");
        Assert.Equal(0, first.Opened);
        Assert.Equal(1, second.Opened);

        var kept = new Calls();
        for (uint id = 100; id < 100 + PopupRegistry.Max + 5; id++) registry.Add(id, kept.Actions());
        Assert.False(registry.Invoke(100, "default"));   // the oldest were dropped
        Assert.True(registry.Invoke(100 + PopupRegistry.Max + 4, "default"));
    }
}
