using System.Text.Json;
using Yaparr.AppCore;

namespace Yaparr.Tests;

public class NotificationLogTests
{
    private const long Now = 1_800_000_000_000;

    private static NotificationLog Log(params NotificationEntry[] entries) => new(entries, () => Now);

    private static NotificationEntry Entry(string chat = "room-a", string key = "m1", long? time = null, bool read = false) =>
        new() { ChatKey = chat, IsRoom = true, Title = "The room", Sender = "Alex", Body = "hi", MessageKey = key, Time = time ?? Now - 1000, Read = read };

    [Fact]
    public void Items_are_newest_first_whatever_order_they_arrive_in()
    {
        var log = Log();
        log.Add(Entry(key: "b", time: Now - 500));
        log.Add(Entry(key: "a", time: Now - 900));   // a message that waited before it was announced
        log.Add(Entry(key: "c", time: Now - 100));
        Assert.Equal(new[] { "c", "b", "a" }, log.Items.Select(e => e.MessageKey));
    }

    [Fact]
    public void The_same_message_is_listed_once()
    {
        var log = Log();
        Assert.True(log.Add(Entry(key: "same")));
        Assert.False(log.Add(Entry(key: "same")));
        Assert.Single(log.Items);
    }

    [Fact]
    public void Entries_without_a_message_key_are_never_taken_for_duplicates()
    {
        var log = Log();
        log.Add(Entry(key: ""));
        log.Add(Entry(key: ""));
        Assert.Equal(2, log.Items.Count);
    }

    [Fact]
    public void An_entry_without_a_time_is_stamped_with_now()
    {
        var log = Log();
        log.Add(new NotificationEntry { ChatKey = "c", Time = 0 });
        Assert.Equal(Now, log.Items[0].Time);
    }

    [Fact]
    public void Opening_a_chat_marks_only_that_chats_entries_seen()
    {
        var log = Log(Entry("room-a", "1"), Entry("room-a", "2"), Entry("room-b", "3"));
        Assert.Equal(3, log.UnreadCount);
        Assert.Equal(2, log.MarkChatRead("room-a"));
        Assert.Equal(1, log.UnreadCount);
        Assert.Equal(0, log.UnreadIn("room-a"));
        Assert.Equal(1, log.UnreadIn("room-b"));
        Assert.Equal(3, log.Items.Count);   // seen, not removed
    }

    [Fact]
    public void Marking_what_is_already_seen_changes_nothing_and_raises_nothing()
    {
        var log = Log(Entry(read: true));
        log.Saved();
        int raised = 0;
        log.Changed += () => raised++;
        Assert.Equal(0, log.MarkChatRead("room-a"));
        Assert.Equal(0, log.MarkAllRead());
        Assert.False(log.MarkRead(log.Items[0].Id));
        Assert.Equal(0, raised);
        Assert.False(log.Dirty);
    }

    [Fact]
    public void Mark_all_seen_remove_and_clear()
    {
        var log = Log(Entry(key: "1"), Entry(key: "2"), Entry(key: "3"));
        var id = log.Items[0].Id;
        Assert.True(log.Remove(id));
        Assert.False(log.Remove(id));
        Assert.Equal(2, log.MarkAllRead());
        Assert.Equal(0, log.UnreadCount);
        log.Clear();
        Assert.Empty(log.Items);
    }

    [Fact]
    public void Leaving_a_chat_removes_what_it_had()
    {
        var log = Log(Entry("gone", "1"), Entry("gone", "2"), Entry("kept", "3"));
        Assert.Equal(2, log.RemoveChat("gone"));
        Assert.Equal("kept", Assert.Single(log.Items).ChatKey);
    }

    [Fact]
    public void Changes_raise_changed_and_mark_the_log_for_saving()
    {
        var log = Log();
        int raised = 0;
        log.Changed += () => raised++;
        log.Add(Entry());
        Assert.Equal(1, raised);
        Assert.True(log.Dirty);
        log.Saved();
        Assert.False(log.Dirty);
    }

    [Fact]
    public void Only_the_newest_two_hundred_are_kept()
    {
        var log = Log();
        for (int i = 0; i < NotificationLog.MaxEntries + 25; i++) log.Add(Entry(key: "k" + i, time: Now - 100_000 + i));
        Assert.Equal(NotificationLog.MaxEntries, log.Items.Count);
        Assert.Equal("k" + (NotificationLog.MaxEntries + 24), log.Items[0].MessageKey);
        Assert.DoesNotContain(log.Items, e => e.MessageKey == "k0");
    }

    [Fact]
    public void What_is_too_old_is_dropped_when_the_list_is_loaded_and_when_it_grows()
    {
        long stale = Now - (long)NotificationLog.KeepFor.TotalMilliseconds - 1;
        var log = Log(Entry(key: "old", time: stale), Entry(key: "new"));
        Assert.Equal("new", Assert.Single(log.Items).MessageKey);
        Assert.True(log.Dirty);   // the file still holds the old one until it is saved again
    }

    [Fact]
    public void An_unknown_kind_survives_a_round_trip_through_the_file()
    {
        var json = JsonSerializer.Serialize(new[] { new NotificationEntry { ChatKey = "c", Kind = "invite", Time = Now - 5, Title = "Come in" } });
        var back = JsonSerializer.Deserialize<List<NotificationEntry>>(json)!;
        var log = Log(back.ToArray());
        Assert.Equal("invite", log.ForSaving[0].Kind);
    }

    [Fact]
    public void The_heading_says_who_and_where()
    {
        Assert.Equal("Alex in The room", NotificationLog.Heading(Entry()));
        Assert.Equal("Sam", NotificationLog.Heading(new NotificationEntry { Sender = "Sam", Title = "Sam", IsRoom = false }));
        Assert.Equal("Sam", NotificationLog.Heading(new NotificationEntry { Sender = "Sam", Title = "Sam", IsRoom = true }));
        Assert.Equal("The room", NotificationLog.Heading(new NotificationEntry { Sender = "", Title = "The room", IsRoom = true }));
    }

    [Fact]
    public void The_file_is_saved_and_loaded_back_and_a_damaged_one_gives_an_empty_list()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-notif-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new AppData(dir);
            Assert.Empty(data.LoadNotifications());
            data.SaveNotifications(new[] { Entry(key: "x") });
            var loaded = data.LoadNotifications();
            Assert.Equal("x", Assert.Single(loaded).MessageKey);
            File.WriteAllText(Path.Combine(dir, "notifications.json"), "not json at all");
            Assert.Empty(data.LoadNotifications());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}

public class InviteNotificationTests
{
    private const long Now = 1_800_000_000_000;
    private static NotificationEntry Invite(string key = "m1", string? outcome = null) =>
        new() { Kind = NotificationKinds.Invite, ChatKey = "peer", Title = "The room", Sender = "Alex", MessageKey = key, Time = Now - 10, Invite = Yaparr.Chat.RoomKey.Generate().ToString(), Outcome = outcome };

    [Fact]
    public void An_invitation_waits_until_it_is_answered_and_then_counts_as_seen()
    {
        var log = new NotificationLog(new[] { Invite() }, () => Now);
        var entry = log.Items[0];
        Assert.True(entry.IsOpenInvite);
        Assert.Single(log.OpenInvites);
        Assert.True(log.SetOutcome(entry.Id, NotificationOutcomes.Joined));
        Assert.False(entry.IsOpenInvite);
        Assert.True(entry.IsInvite);
        Assert.True(entry.Read);
        Assert.Empty(log.OpenInvites);
        Assert.False(log.SetOutcome("nope", NotificationOutcomes.Declined));
    }

    [Fact]
    public void An_entry_of_kind_invite_without_a_key_is_not_an_invitation()
    {
        var entry = new NotificationEntry { Kind = NotificationKinds.Invite, Invite = null };
        Assert.False(entry.IsInvite);
        Assert.False(entry.IsOpenInvite);
    }

    [Fact]
    public void The_heading_of_an_invitation_says_who_invited_you_to_what()
    {
        Assert.Equal("Alex invited you to The room", NotificationLog.Heading(Invite()));
        Assert.Equal("Someone invited you to a room", NotificationLog.Heading(new NotificationEntry { Kind = NotificationKinds.Invite, Invite = "x" }));
    }

    [Fact]
    public void An_answered_invitation_survives_the_file()
    {
        var json = JsonSerializer.Serialize(new[] { Invite(outcome: NotificationOutcomes.Declined) });
        var back = new NotificationLog(JsonSerializer.Deserialize<List<NotificationEntry>>(json)!, () => Now).Items[0];
        Assert.Equal(NotificationOutcomes.Declined, back.Outcome);
        Assert.NotNull(back.Invite);
    }
}
