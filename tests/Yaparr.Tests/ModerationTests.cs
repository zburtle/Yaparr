using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;
using Yaparr.Voice;

namespace Yaparr.Tests;

public class ModerationTests
{
    private sealed record Room(ChatNode Owner, ChatNode B, ChatNode C, ChatRoom Ro, ChatRoom Rb, ChatRoom Rc, InMemoryDiscoveryHub Hub, string Invite);

    private static async Task<Room> Trio()
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "owner");
        var b = TestUtil.NewNode(hub, "bob");
        var c = TestUtil.NewNode(hub, "carol");
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 2 && rb.Members.Count >= 2 && rc.Members.Count >= 2, 30_000);
        return new Room(a, b, c, ra, rb, rc, hub, key.ToString());
    }

    private static List<string> Collect(ChatRoom room)
    {
        var texts = new List<string>();
        room.MessageReceived += m => { lock (texts) texts.Add(m.Text); };
        return texts;
    }

    private static bool Has(List<string> texts, string text) { lock (texts) return texts.Contains(text); }

    [Fact]
    public void An_order_is_only_good_when_its_signer_really_signed_it()
    {
        var admin = PeerIdentity.Generate();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var target = PeerIdentity.Generate().AccountId.Value;
        var ban = SignedMod.Create(admin, "room", SignedMod.Ban, target, "carol", now);
        Assert.True(SignedMod.Verify(ban, "room", out var signer));
        Assert.Equal(admin.AccountId, signer);
        Assert.False(SignedMod.Verify(ban, "another room", out _));
        Assert.False(SignedMod.Verify(ban with { Kind = SignedMod.Unban }, "room", out _));
        Assert.False(SignedMod.Verify(ban with { Target = PeerIdentity.Generate().AccountId.Value }, "room", out _));
        Assert.False(SignedMod.Verify(ban with { Kind = SignedMod.DeleteMessage }, "room", out _)); // a message key is 32 characters, not 64
        Assert.False(SignedMod.Verify(SignedMod.Create(admin, "room", SignedMod.Ban, target, "x", now + 3_600_000), "room", out _));
    }

    [Fact]
    public async Task A_banned_member_disappears_for_everyone_and_their_messages_stop_and_come_back_when_unbanned()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var atOwner = Collect(t.Ro); var atBob = Collect(t.Rb);

        await t.Rc.SendAsync("before the ban");
        await TestUtil.WaitFor(() => Has(atOwner, "before the ban") && Has(atBob, "before the ban"), 20_000);

        t.Ro.Ban(t.C.AccountId, "carol");
        await TestUtil.WaitFor(() => t.Rb.IsBanned(t.C.AccountId) && t.Ro.IsBanned(t.C.AccountId), 20_000);
        Assert.Equal("carol", t.Rb.Banned.Single().Name);
        // Her old messages are hidden too, and she is not a member for anyone.
        Assert.DoesNotContain(t.Rb.GetHistory(), m => m.Text == "before the ban");
        await TestUtil.WaitFor(() => t.Rb.Members.All(m => m.Account != t.C.AccountId) && t.Ro.Members.All(m => m.Account != t.C.AccountId), 20_000);

        await t.Rc.SendAsync("after the ban");
        await Task.Delay(1500);
        Assert.False(Has(atOwner, "after the ban"));
        Assert.False(Has(atBob, "after the ban"));

        t.Ro.Unban(t.C.AccountId);
        await TestUtil.WaitFor(() => !t.Rb.IsBanned(t.C.AccountId), 20_000);
        await TestUtil.WaitFor(() => t.Rb.Members.Any(m => m.Account == t.C.AccountId) && t.Ro.Members.Any(m => m.Account == t.C.AccountId), 30_000);
        await t.Rc.SendAsync("welcome back");
        await TestUtil.WaitFor(() => Has(atOwner, "welcome back") && Has(atBob, "welcome back"), 20_000);
    }

    [Fact]
    public async Task Only_the_owner_and_admins_can_ban_and_an_admin_cannot_ban_an_admin_or_the_owner()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;

        Assert.Throws<InvalidOperationException>(() => t.Rb.Ban(t.C.AccountId, "carol"));
        Assert.Throws<InvalidOperationException>(() => t.Rc.DeleteSound("0000000000000000"));

        t.Ro.SetAdmin(t.B.AccountId, true);
        await TestUtil.WaitFor(() => t.Rb.MyRole == RoomRole.Admin, 20_000);
        Assert.Throws<InvalidOperationException>(() => t.Rb.Ban(t.Owner.AccountId, "owner"));
        Assert.Throws<InvalidOperationException>(() => t.Rb.Ban(t.B.AccountId, "me"));
        Assert.Throws<InvalidOperationException>(() => t.Ro.Ban(t.B.AccountId, "bob")); // remove the admin first
        Assert.Throws<InvalidOperationException>(() => t.Ro.Ban(t.Owner.AccountId, "me"));
    }

    [Fact]
    public async Task An_admins_ban_counts_and_goes_away_with_the_admin()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        t.Ro.SetAdmin(t.B.AccountId, true);
        await TestUtil.WaitFor(() => t.Rb.MyRole == RoomRole.Admin && t.Rc.RoleOf(t.B.AccountId) == RoomRole.Admin, 20_000);

        t.Rb.Ban(t.C.AccountId, "carol");
        await TestUtil.WaitFor(() => t.Ro.IsBanned(t.C.AccountId), 20_000);

        // The owner's word beats an admin's: bob cannot lift a ban the owner made.
        t.Ro.Ban(t.C.AccountId, "carol");
        await TestUtil.WaitFor(() => t.Ro.Banned.Count == 1, 5_000);
        t.Rb.Unban(t.C.AccountId);
        await Task.Delay(1000);
        Assert.True(t.Ro.IsBanned(t.C.AccountId));
        Assert.True(t.Rb.IsBanned(t.C.AccountId));

        // Where the owner has said nothing, a ban lasts only while its admin is one.
        var other = PeerIdentity.Generate().AccountId;
        t.Rb.Ban(other, "mallory");
        await TestUtil.WaitFor(() => t.Ro.IsBanned(other), 20_000);
        t.Ro.SetAdmin(t.B.AccountId, false);
        await TestUtil.WaitFor(() => !t.Ro.IsBanned(other), 20_000);
    }

    [Fact]
    public async Task A_message_an_admin_deletes_goes_for_everyone_and_does_not_come_back_with_the_history()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var atOwner = Collect(t.Ro);
        await t.Rc.SendAsync("something bad");
        await t.Rc.SendAsync("something fine");
        await TestUtil.WaitFor(() => Has(atOwner, "something bad") && Has(atOwner, "something fine"), 20_000);

        var bad = t.Ro.GetHistory().Single(m => m.Text == "something bad");
        Assert.Throws<InvalidOperationException>(() => t.Rb.DeleteMessage(bad));
        t.Ro.DeleteMessage(bad);
        await TestUtil.WaitFor(() => t.Rb.GetHistory().All(m => m.Text != "something bad") && t.Rc.GetHistory().All(m => m.Text != "something bad"), 20_000);
        Assert.Contains(t.Rb.GetHistory(), m => m.Text == "something fine");

        var d = TestUtil.NewNode(t.Hub, "dave");
        using var ____ = d;
        var rd = await d.JoinRoomAsync(RoomKey.Parse(t.Invite), "lobby");
        await TestUtil.WaitFor(() => rd.GetHistory().Any(m => m.Text == "something fine"), 30_000);
        await Task.Delay(500);
        Assert.DoesNotContain(rd.GetHistory(), m => m.Text == "something bad");
    }

    [Fact]
    public async Task An_admin_can_remove_anyones_sound()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var clip = SoundClip.FromPcm(Enumerable.Range(0, 48000).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray());
        var added = t.Rc.AddSound("airhorn", clip);
        await TestUtil.WaitFor(() => t.Ro.Sounds.Any(s => s.Name == "airhorn") && t.Rb.Sounds.Any(s => s.Name == "airhorn"), 20_000);

        Assert.Throws<InvalidOperationException>(() => t.Rb.DeleteSound(added.Id));
        t.Ro.DeleteSound(added.Id);
        await TestUtil.WaitFor(() => t.Rb.Sounds.All(s => s.Name != "airhorn") && t.Rc.Sounds.All(s => s.Name != "airhorn"), 20_000);
        Assert.Null(t.Rb.GetSound(added.Id));
    }

    [Fact]
    public async Task The_bans_are_remembered_across_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-mod-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new InMemoryDiscoveryHub();
            var identity = PeerIdentity.Generate();
            var key = RoomKey.Generate(identity.AccountId);
            var target = PeerIdentity.Generate().AccountId;
            ChatNodeOptions Options() => new()
            {
                Identity = identity, RolesDirectory = dir, DisplayName = "owner",
                DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
                DiscoveryInterval = TimeSpan.FromMilliseconds(300), Network = TestUtil.FastNetwork,
            };
            using (var first = new ChatNode(Options()))
            {
                var room = await first.JoinRoomAsync(key, "lobby");
                room.Ban(target, "mallory");
                Assert.True(room.IsBanned(target));
            }
            using var second = new ChatNode(Options());
            var again = await second.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
            Assert.True(again.IsBanned(target));
            Assert.Equal("mallory", again.Banned.Single().Name);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_room_without_an_owner_has_no_moderation()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        var room = await a.JoinRoomAsync("old room");
        Assert.Throws<InvalidOperationException>(() => room.Ban(PeerIdentity.Generate().AccountId, "x"));
        Assert.Empty(room.Banned);
    }
}
