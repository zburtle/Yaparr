using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class SettingsSyncMappingTests
{
    private static readonly string Room = new('a', 64), Other = new('b', 64);

    [Fact]
    public void Only_what_is_not_the_default_is_an_entry_and_a_list_is_one_entry_for_each_item()
    {
        var s = new AppSettings { MutedRooms = { Room, Other }, RoomSounds = { [Room] = "Bell" }, NotifyOnlyMentions = true };
        var snap = SettingsSync.Snapshot(s);
        Assert.Equal("1", snap["muted:" + Room]);
        Assert.Equal("1", snap["muted:" + Other]);
        Assert.Equal("Bell", snap["room-sound:" + Room]);
        Assert.Equal("1", snap["only-mentions"]);
        Assert.False(snap.ContainsKey("message-sound"));          // the default sound: no entry
        Assert.False(snap.ContainsKey("notify-messages"));        // on is the default
        Assert.Equal(4, snap.Count);
    }

    [Fact]
    public void A_look_or_a_device_choice_is_never_synced()
    {
        var s = new AppSettings { Theme = "Dark", VoiceInput = "Mic 3", VoiceBitrateKbps = 128, CloseToTray = false, VpnEnabled = true, DesktopPopups = false };
        Assert.Empty(SettingsSync.Snapshot(s));
        Assert.False(SettingsSync.Apply(s, "theme", "Light"));    // a name this version does not know changes nothing
        Assert.Equal("Dark", s.Theme);
    }

    [Fact]
    public void A_change_from_another_device_is_applied_and_a_removal_puts_it_back_to_the_default()
    {
        var s = new AppSettings();
        Assert.True(SettingsSync.Apply(s, "muted:" + Room, "1"));
        Assert.False(SettingsSync.Apply(s, "muted:" + Room, "1"));                 // already so
        Assert.True(SettingsSync.Apply(s, "room-sound:" + Room, "Pop"));
        Assert.True(SettingsSync.Apply(s, "message-sound", "Drop"));
        Assert.True(SettingsSync.Apply(s, "notify-messages", "0"));
        Assert.Contains(Room, s.MutedRooms);
        Assert.Equal("Pop", s.RoomSounds[Room]);
        Assert.Equal("Drop", s.MessageSound);
        Assert.False(s.NotifyMessages);

        Assert.True(SettingsSync.Apply(s, "muted:" + Room, null));
        Assert.True(SettingsSync.Apply(s, "message-sound", null));
        Assert.True(SettingsSync.Apply(s, "notify-messages", null));
        Assert.Empty(s.MutedRooms);
        Assert.Equal("Chime", s.MessageSound);
        Assert.True(s.NotifyMessages);
    }

    [Fact]
    public void Changes_are_sent_once_and_a_value_the_account_already_holds_is_not_sent_again()
    {
        var before = new Dictionary<string, string> { ["muted:" + Room] = "1" };
        var now = new Dictionary<string, string> { ["muted:" + Other] = "1", ["only-mentions"] = "1" };
        var held = new Dictionary<string, string> { ["muted:" + Room] = "1", ["only-mentions"] = "1" };
        var changes = SettingsSync.Changes(before, now, held);
        Assert.Equal("1", changes["muted:" + Other]);                 // new here
        Assert.Null(changes["muted:" + Room]);                        // back to the default, and the account holds it: a removal
        Assert.False(changes.ContainsKey("only-mentions"));          // the account has it already
        Assert.Equal(2, changes.Count);
    }
}

public class SettingsSyncDevicesTests
{
    private static readonly string Room = new('c', 64);

    private sealed class Pair : IDisposable
    {
        private readonly PeerIdentity _firstId = PeerIdentity.Generate();
        private readonly PeerIdentity _secondId;
        public readonly InMemoryDiscoveryHub Hub = new();
        public ChatNode First = null!, Second = null!;

        public Pair() => _secondId = _firstId.CreateDevice();

        public static async Task<Pair> StartAsync()
        {
            var p = new Pair();
            p.First = TestUtil.NewNode(p.Hub, "alice", identity: p._firstId);
            p.Second = TestUtil.NewNode(p.Hub, "alice", identity: p._secondId);
            var r1 = await p.First.JoinRoomAsync("lobby");
            var r2 = await p.Second.JoinRoomAsync("lobby");
            await TestUtil.WaitFor(() => r1.Members.Count == 1 && r2.Members.Count == 1);
            await TestUtil.WaitFor(() => p.First.IsMyDevice(p.Second.Id) && p.Second.IsMyDevice(p.First.Id));
            return p;
        }

        public void Dispose()
        {
            First.Dispose(); Second.Dispose();
            _firstId.Dispose(); _secondId.Dispose();
        }
    }

    [Fact]
    public async Task Muting_a_room_on_one_device_mutes_it_on_the_other_and_unmuting_does_too()
    {
        using var d = await Pair.StartAsync();
        var firstSettings = new AppSettings();
        var secondSettings = new AppSettings();
        var first = new SettingsPush(d.First);
        var second = new SettingsPush(d.Second);
        first.Seed(firstSettings);
        second.Seed(secondSettings);
        d.Second.SettingChanged += (name, value) => second.Remote(secondSettings, name, value);

        firstSettings.MutedRooms.Add(Room);
        firstSettings.RoomSounds[Room] = "Bell";
        first.Local(firstSettings);
        await TestUtil.WaitFor(() => secondSettings.MutedRooms.Contains(Room) && secondSettings.RoomSounds.GetValueOrDefault(Room) == "Bell");

        // What came from the other device is not sent back as a change of this one (the entry stays as it was, one write).
        second.Local(secondSettings);
        Assert.Equal("1", d.First.AccountSettings["muted:" + Room]);

        firstSettings.MutedRooms.Clear();
        first.Local(firstSettings);
        await TestUtil.WaitFor(() => secondSettings.MutedRooms.Count == 0);
        Assert.Equal("Bell", secondSettings.RoomSounds[Room]);        // the sound was left alone
    }

    [Fact]
    public async Task A_device_that_starts_takes_in_what_the_account_holds_and_gives_it_what_it_has()
    {
        using var d = await Pair.StartAsync();
        d.First.SetAccountSetting("muted:" + Room, "1");
        await TestUtil.WaitFor(() => d.Second.AccountSettings.ContainsKey("muted:" + Room));

        var settings = new AppSettings { NotifyOnlyMentions = true };      // had this before it joined the account
        var push = new SettingsPush(d.Second);
        Assert.True(push.Seed(settings));
        Assert.Contains(Room, settings.MutedRooms);                        // the account's wins where it has a value
        Assert.True(settings.NotifyOnlyMentions);                          // and what the account lacks is kept
        await TestUtil.WaitFor(() => d.First.AccountSettings.ContainsKey("only-mentions"));
    }
}
