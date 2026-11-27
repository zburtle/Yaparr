using Yaparr.Chat;
using Yaparr.Desktop;

namespace Yaparr.Tests;

public sealed class ChannelListTests
{
    private static ConversationVm RoomWith(params string[] names)
    {
        var conv = new ConversationVm("Room", isRoom: true, _ => Task.FromResult(""));
        conv.SetChannels(names.Select((n, i) => new ChannelInfo(i == 0 ? RoomChannels.DefaultText : "c" + i, n, ChannelKind.Text, i, i == 0)).ToList());
        return conv;
    }

    [Fact]
    public void A_muted_channel_keeps_its_own_count_but_adds_nothing_to_the_room_unless_it_names_you()
    {
        var conv = RoomWith("general", "memes");
        var memes = conv.Channels.Single(c => c.Name == "memes");
        memes.Muted = true;

        conv.NoteUnread("c1");
        conv.NoteUnread("c1");
        Assert.Equal(2, memes.Unread);
        Assert.Equal(0, conv.Unread);
        Assert.False(conv.Mentioned);

        conv.NoteUnread("c1", mentioned: true);
        Assert.Equal(3, memes.Unread);
        Assert.Equal(1, conv.Unread);
        Assert.True(conv.Mentioned);
    }

    [Fact]
    public void Opening_a_channel_takes_off_what_it_added_to_the_room()
    {
        var conv = RoomWith("general", "memes", "news");
        conv.NoteUnread("c1");
        conv.NoteUnread("c2");
        conv.NoteUnread("c2");
        Assert.Equal(3, conv.Unread);
        conv.SelectChannel("c2");
        Assert.Equal(1, conv.Unread);
        conv.SelectChannel("c1");
        Assert.Equal(0, conv.Unread);
    }

    [Fact]
    public void Muting_a_channel_that_has_news_takes_it_out_of_the_rooms_count()
    {
        var conv = RoomWith("general", "memes");
        conv.NoteUnread("c1");
        conv.NoteUnread("c1");
        Assert.Equal(2, conv.Unread);
        conv.Channels.Single(c => c.Name == "memes").Muted = true;
        conv.RecountUnread();
        Assert.Equal(0, conv.Unread);
        conv.Channels.Single(c => c.Name == "memes").Muted = false;
        conv.RecountUnread();
        Assert.Equal(2, conv.Unread);
    }

    [Fact]
    public void The_label_shows_a_pin_and_a_quiet_dot_for_a_muted_channel()
    {
        var conv = RoomWith("general", "memes");
        var memes = conv.Channels.Single(c => c.Name == "memes");
        memes.Unread = 4;
        Assert.Equal("# memes  (4)", memes.Label);
        memes.Pinned = true;
        Assert.StartsWith("\U0001F4CC ", memes.Label);
        memes.Muted = true;
        Assert.DoesNotContain("(4)", memes.Label);
        Assert.Contains("•", memes.Label);
        Assert.True(memes.Fade < 1);
        memes.Selected = true;
        Assert.Equal(1, memes.Fade);
    }

    private static ChannelSwitcher.Item Item(string room, string channel, int unread = 0, long last = 0, bool here = false) =>
        new(null!, null!, null!, room, "# " + channel, "", unread, last, here);

    [Fact]
    public void The_switcher_finds_a_channel_by_words_from_its_room_or_its_own_name()
    {
        var all = new[] { Item("Fellas", "general"), Item("Fellas", "critter pics"), Item("Work", "general") };
        Assert.Equal(3, ChannelSwitcher.Matches(all, "").Count);
        Assert.Equal(2, ChannelSwitcher.Matches(all, "general").Count);
        var one = ChannelSwitcher.Matches(all, "fellas critter");
        Assert.Single(one);
        Assert.Equal("# critter pics", one[0].ChannelName);
        Assert.Empty(ChannelSwitcher.Matches(all, "nothing like it"));
    }

    [Fact]
    public void The_switcher_lists_channels_with_news_first_then_the_room_on_show_then_the_latest()
    {
        var all = new[]
        {
            Item("A", "old", last: 1),
            Item("A", "new", last: 9),
            Item("B", "here", last: 2, here: true),
            Item("A", "news", last: 0, unread: 3),
        };
        Assert.Equal(new[] { "# news", "# here", "# new", "# old" }, ChannelSwitcher.Matches(all, "").Select(i => i.ChannelName));
    }

    [Fact]
    public void The_list_shows_loose_channels_and_each_category_with_its_own_and_a_folded_one_shows_its_news_in_the_header()
    {
        var conv = new ConversationVm("Room", isRoom: true, _ => Task.FromResult(""));
        conv.SetChannels(new[]
        {
            new ChannelInfo(RoomChannels.DefaultText, "general", ChannelKind.Text, 0, true),
            new ChannelInfo("c2", "chess", ChannelKind.Text, 2, false, "Games"),
            new ChannelInfo("c3", "Lounge", ChannelKind.Voice, 3, false, "Games"),
            new ChannelInfo("c4", "memes", ChannelKind.Text, 4, false),
        });
        var chess = conv.Channels.Single(c => c.Name == "chess");
        Assert.Equal("Games", chess.Category);
        var games = new[] { chess, conv.Channels.Single(c => c.Name == "Lounge") };
        conv.ApplyLayout(new[] { conv.Channels[0], conv.Channels.Single(c => c.Name == "memes") }, Array.Empty<ChannelVm>(),
            new[] { new ConversationVm.CategoryLayout("Games", Open: true, games) });

        Assert.True(conv.ShowCategories);
        Assert.Equal(new[] { "general", "memes" }, conv.TextChannels.Select(c => c.Name));
        var group = Assert.Single(conv.CategoryGroups);
        Assert.Equal(new[] { "chess", "Lounge" }, group.Channels.Select(c => c.Name));
        Assert.Equal("▾ GAMES", group.Header);

        chess.Unread = 3;
        conv.ApplyLayout(conv.TextChannels.ToList(), Array.Empty<ChannelVm>(), new[] { new ConversationVm.CategoryLayout("Games", Open: false, games) });
        Assert.Same(group, conv.CategoryGroups.Single());   // the same header goes on, folded
        Assert.Equal("▸ GAMES  (3)", group.Header);
        chess.Muted = true;
        Assert.Equal("▸ GAMES", group.Header);   // what a muted channel holds does not show

        conv.ApplyLayout(conv.TextChannels.ToList(), Array.Empty<ChannelVm>(), Array.Empty<ConversationVm.CategoryLayout>());
        Assert.False(conv.ShowCategories);
    }
}
