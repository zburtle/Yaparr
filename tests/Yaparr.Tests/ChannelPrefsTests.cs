using Yaparr.AppCore;
using Yaparr.Chat;

namespace Yaparr.Tests;

public sealed class ChannelPrefsTests
{
    private const string Room = "ab12";

    private static List<string> Arranged(AppSettings s, string[] channels, Func<string, long>? last = null) =>
        ChannelPrefs.Arrange(s, Room, channels, c => c, last ?? (_ => 0));

    [Fact]
    public void Without_any_choices_the_rooms_own_order_is_kept()
    {
        Assert.Equal(new[] { "a", "b", "c" }, Arranged(new AppSettings(), new[] { "a", "b", "c" }));
    }

    [Fact]
    public void Pinned_channels_come_first_and_keep_the_rooms_order_among_themselves()
    {
        var s = new AppSettings();
        ChannelPrefs.Set(s, Room, "c", ChannelFlag.Pinned, true);
        ChannelPrefs.Set(s, Room, "b", ChannelFlag.Pinned, true);
        Assert.Equal(new[] { "b", "c", "a", "d" }, Arranged(s, new[] { "a", "b", "c", "d" }));
    }

    [Fact]
    public void Most_recent_first_orders_each_group_by_activity_and_ties_keep_the_rooms_order()
    {
        var s = new AppSettings();
        ChannelPrefs.SetRecentFirst(s, Room, true);
        ChannelPrefs.Set(s, Room, "d", ChannelFlag.Pinned, true);
        var times = new Dictionary<string, long> { ["a"] = 5, ["b"] = 9, ["c"] = 5, ["d"] = 1 };
        Assert.Equal(new[] { "d", "b", "a", "c" }, Arranged(s, new[] { "a", "b", "c", "d" }, c => times[c]));
    }

    [Fact]
    public void A_flag_is_for_one_room_and_one_channel_only()
    {
        var s = new AppSettings();
        Assert.True(ChannelPrefs.Set(s, Room, "a", ChannelFlag.Muted, true));
        Assert.False(ChannelPrefs.Set(s, Room, "a", ChannelFlag.Muted, true));
        Assert.True(ChannelPrefs.Has(s, Room, "a", ChannelFlag.Muted));
        Assert.False(ChannelPrefs.Has(s, Room, "b", ChannelFlag.Muted));
        Assert.False(ChannelPrefs.Has(s, "other", "a", ChannelFlag.Muted));
        Assert.False(ChannelPrefs.Has(s, Room, "a", ChannelFlag.Pinned));
        Assert.True(ChannelPrefs.Set(s, Room, "a", ChannelFlag.Muted, false));
        Assert.False(ChannelPrefs.Set(s, Room, "a", ChannelFlag.Muted, false));
    }

    [Fact]
    public void A_muted_channel_only_counts_a_message_that_names_you()
    {
        var s = new AppSettings();
        Assert.True(ChannelPrefs.Counts(s, Room, "a", mentionsMe: false));
        ChannelPrefs.Set(s, Room, "a", ChannelFlag.Muted, true);
        Assert.False(ChannelPrefs.Counts(s, Room, "a", mentionsMe: false));
        Assert.True(ChannelPrefs.Counts(s, Room, "a", mentionsMe: true));
        Assert.True(ChannelPrefs.Counts(s, Room, "b", mentionsMe: false));
    }

    [Fact]
    public void A_category_is_folded_for_one_room_and_one_name_whatever_its_capitals()
    {
        var s = new AppSettings();
        Assert.False(ChannelPrefs.IsCollapsed(s, Room, "Games"));
        Assert.True(ChannelPrefs.SetCollapsed(s, Room, "Games", true));
        Assert.False(ChannelPrefs.SetCollapsed(s, Room, "games", true));
        Assert.True(ChannelPrefs.IsCollapsed(s, Room, "GAMES"));
        Assert.False(ChannelPrefs.IsCollapsed(s, Room, "News"));
        Assert.False(ChannelPrefs.IsCollapsed(s, "other", "Games"));
        Assert.True(ChannelPrefs.SetCollapsed(s, Room, "Games", false));
        Assert.False(ChannelPrefs.IsCollapsed(s, Room, "Games"));
    }

    [Fact]
    public void The_choices_travel_to_another_device_and_back_to_the_default()
    {
        var room = new string('f', 64);
        var channel = new string('e', 16);
        var first = new AppSettings();
        ChannelPrefs.Set(first, room, channel, ChannelFlag.Pinned, true);
        ChannelPrefs.Set(first, room, channel, ChannelFlag.Muted, true);
        ChannelPrefs.SetRecentFirst(first, room, true);
        ChannelPrefs.SetCollapsed(first, room, new string('N', RoomChannels.MaxCategoryName), true);

        var sent = SettingsSync.Snapshot(first);
        Assert.Equal(4, sent.Count);   // every one fits the limits of an account entry

        var second = new AppSettings();
        foreach (var (name, value) in sent) Assert.True(SettingsSync.Apply(second, name, value));
        Assert.True(ChannelPrefs.Has(second, room, channel, ChannelFlag.Pinned));
        Assert.True(ChannelPrefs.Has(second, room, channel, ChannelFlag.Muted));
        Assert.True(ChannelPrefs.RecentFirst(second, room));
        Assert.True(ChannelPrefs.IsCollapsed(second, room, new string('N', RoomChannels.MaxCategoryName)));

        foreach (var name in sent.Keys) Assert.True(SettingsSync.Apply(second, name, null));
        Assert.Empty(second.PinnedChannels);
        Assert.Empty(second.MutedChannels);
        Assert.Empty(second.RecentFirstRooms);
        Assert.Empty(second.CollapsedCategories);
    }

    [Fact]
    public void A_channel_flag_changed_on_one_device_reaches_the_other_through_the_account()
    {
        var room = new string('1', 64);
        var channel = RoomChannels.DefaultText;
        var settings = new AppSettings();
        ChannelPrefs.Set(settings, room, channel, ChannelFlag.Muted, true);
        var held = new Dictionary<string, string>();
        var changes = SettingsSync.Changes(new Dictionary<string, string>(), SettingsSync.Snapshot(settings), held);
        Assert.Single(changes);
        Assert.Equal("1", changes.Values.Single());
    }
}
