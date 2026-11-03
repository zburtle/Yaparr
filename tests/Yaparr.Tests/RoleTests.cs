using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class RoleTests
{
    [Fact]
    public void An_invite_can_name_the_owner_and_old_invites_still_work()
    {
        var owner = PeerIdentity.Generate();
        var key = RoomKey.Generate(owner.AccountId);
        var parsed = RoomKey.Parse(key.ToString());
        Assert.Equal(owner.AccountId, parsed.Owner);
        Assert.Equal(key.Topic, parsed.Topic);

        var old = RoomKey.Generate();
        Assert.Null(RoomKey.Parse(old.ToString()).Owner);
        Assert.Equal(43, old.ToString().Length);
        Assert.Throws<FormatException>(() => RoomKey.Parse(old.ToString() + "AAAA"));
    }

    [Fact]
    public void The_same_secret_with_another_owner_is_another_room()
    {
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        RoomKey With(PeerId owner) => RoomKey.Parse(System.Buffers.Text.Base64Url.EncodeToString(secret.Concat(Convert.FromHexString(owner.Value)).ToArray()));
        var a = With(PeerIdentity.Generate().AccountId);
        var b = With(PeerIdentity.Generate().AccountId);
        var plain = RoomKey.Parse(System.Buffers.Text.Base64Url.EncodeToString(secret));
        Assert.NotEqual(a.Topic, b.Topic);
        Assert.NotEqual(a.Topic, plain.Topic);
    }

    [Fact]
    public void Only_the_owner_can_sign_an_admin_statement()
    {
        var owner = PeerIdentity.Generate();
        var other = PeerIdentity.Generate();
        var subject = PeerIdentity.Generate().AccountId;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var good = SignedRole.Create(owner, "room", subject, true, now);
        Assert.True(SignedRole.Verify(good, "room", owner.AccountId));
        Assert.False(SignedRole.Verify(good, "another room", owner.AccountId));
        Assert.False(SignedRole.Verify(good with { Admin = false }, "room", owner.AccountId));
        Assert.False(SignedRole.Verify(good with { Subject = other.AccountId.Value }, "room", owner.AccountId));
        Assert.False(SignedRole.Verify(SignedRole.Create(other, "room", subject, true, now), "room", owner.AccountId));
        Assert.False(SignedRole.Verify(SignedRole.Create(owner, "room", subject, true, now + 3_600_000), "room", owner.AccountId));
    }

    private static async Task<(ChatNode Owner, ChatNode B, ChatNode C, ChatRoom Ro, ChatRoom Rb, ChatRoom Rc)> Trio(InMemoryDiscoveryHub hub, string? ownerRoles = null)
    {
        var a = TestUtil.NewNode(hub, "owner", rolesDirectory: ownerRoles);
        var b = TestUtil.NewNode(hub, "bob");
        var c = TestUtil.NewNode(hub, "carol");
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 2 && rb.Members.Count >= 2 && rc.Members.Count >= 2, 30_000);
        return (a, b, c, ra, rb, rc);
    }

    [Fact]
    public async Task The_owner_appoints_and_removes_an_admin_and_the_whole_room_sees_it()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, c, ra, rb, rc) = await Trio(hub);
        using var _ = a; using var __ = b; using var ___ = c;

        Assert.Equal(RoomRole.Owner, ra.MyRole);
        Assert.Equal(RoomRole.Member, rb.MyRole);
        Assert.Equal(a.AccountId, rb.Owner);

        ra.SetAdmin(b.AccountId, true);
        await TestUtil.WaitFor(() => rb.MyRole == RoomRole.Admin && rc.RoleOf(b.AccountId) == RoomRole.Admin, 20_000);
        Assert.Equal(RoomRole.Owner, rc.RoleOf(a.AccountId));
        Assert.Equal(RoomRole.Member, rc.MyRole);
        Assert.Equal(new[] { b.AccountId }, rc.Admins);

        // Nobody but the owner can change the roles, an admin included.
        Assert.Throws<InvalidOperationException>(() => rb.SetAdmin(c.AccountId, true));
        Assert.Throws<InvalidOperationException>(() => rc.SetAdmin(c.AccountId, true));
        Assert.Throws<InvalidOperationException>(() => ra.SetAdmin(a.AccountId, true));

        ra.SetAdmin(b.AccountId, false);
        await TestUtil.WaitFor(() => rb.MyRole == RoomRole.Member && rc.RoleOf(b.AccountId) == RoomRole.Member, 20_000);
        Assert.Empty(rc.Admins);
    }

    [Fact]
    public async Task Everyone_is_only_named_by_an_admin_or_the_owner()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, c, ra, rb, rc) = await Trio(hub);
        using var _ = a; using var __ = b; using var ___ = c;
        var atCarol = new List<ChatMessage>();
        rc.MessageReceived += m => { lock (atCarol) atCarol.Add(m); };

        await ra.SendAsync("lunch is here", null, null, new[] { Mention.Everyone });
        await rb.SendAsync("free pizza", null, null, new[] { Mention.Everyone });
        await TestUtil.WaitFor(() => { lock (atCarol) return atCarol.Count == 2; }, 20_000);
        ChatMessage fromOwner, fromMember;
        lock (atCarol) { fromOwner = atCarol.First(m => m.Body == "lunch is here"); fromMember = atCarol.First(m => m.Body == "free pizza"); }

        Assert.Contains(fromOwner.Parts.Mentions, m => m.IsEveryone);
        Assert.True(rc.Names(fromOwner, c.AccountId));
        Assert.False(rc.Names(fromMember, c.AccountId));

        ra.SetAdmin(b.AccountId, true);
        await TestUtil.WaitFor(() => rc.RoleOf(b.AccountId) == RoomRole.Admin, 20_000);
        Assert.True(rc.Names(fromMember, c.AccountId));
    }

    [Fact]
    public async Task Someone_who_joins_later_learns_who_the_admins_are()
    {
        var hub = new InMemoryDiscoveryHub();
        var (a, b, c, ra, rb, rc) = await Trio(hub);
        using var _ = a; using var __ = b; using var ___ = c;
        ra.SetAdmin(b.AccountId, true);
        await TestUtil.WaitFor(() => rc.RoleOf(b.AccountId) == RoomRole.Admin, 20_000);

        var d = TestUtil.NewNode(hub, "dave");
        using var ____ = d;
        var rd = await d.JoinRoomAsync(RoomKey.Parse(ra.SecretText), "lobby");
        await TestUtil.WaitFor(() => rd.RoleOf(b.AccountId) == RoomRole.Admin, 30_000);
    }

    [Fact]
    public async Task The_admins_are_remembered_across_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-roles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new InMemoryDiscoveryHub();
            var identity = PeerIdentity.Generate();
            var key = RoomKey.Generate(identity.AccountId);
            var subject = PeerIdentity.Generate().AccountId;
            using (var first = new ChatNode(OwnerOptions(hub, identity, dir)))
            {
                var room = await first.JoinRoomAsync(key, "lobby");
                room.SetAdmin(subject, true);
            }
            using var second = new ChatNode(OwnerOptions(hub, identity, dir));
            var again = await second.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
            Assert.Equal(RoomRole.Admin, again.RoleOf(subject));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static ChatNodeOptions OwnerOptions(InMemoryDiscoveryHub hub, PeerIdentity identity, string dir) => new()
    {
        Identity = identity,
        RolesDirectory = dir,
        DisplayName = "owner",
        DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
    };

    [Fact]
    public async Task A_room_from_an_earlier_version_has_no_owner_and_no_admins()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        var room = await a.JoinRoomAsync("old room");
        Assert.Null(room.Owner);
        Assert.Equal(RoomRole.Member, room.MyRole);
        Assert.Throws<InvalidOperationException>(() => room.SetAdmin(PeerIdentity.Generate().AccountId, true));
    }

    [Fact]
    public async Task A_change_beats_the_statement_it_replaces_even_when_the_owners_clock_runs_slow()
    {
        var hub = new InMemoryDiscoveryHub();
        var identity = PeerIdentity.Generate();
        using var a = TestUtil.NewNode(hub, "owner", identity: identity);
        var room = await a.JoinRoomAsync(RoomKey.Generate(a.AccountId), "lobby");
        var subject = PeerIdentity.Generate().AccountId;

        // A grant dated two minutes ahead of this clock, as one signed by another of the owner's devices with a fast clock would be.
        var ahead = SignedRole.Create(identity, room.Key, subject, true, DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());
        lock (room.RoleGate) room.RoleEntries[ahead.Subject] = ahead;
        Assert.Equal(RoomRole.Admin, room.RoleOf(subject));

        room.SetAdmin(subject, false);
        Assert.Equal(RoomRole.Member, room.RoleOf(subject));
    }

    [Fact]
    public void A_peer_that_sends_admin_lists_too_fast_is_held_back()
    {
        var rate = new PeerRate(new PeerLimits { RoleListsBurst = 3, RoleListsPerSecond = 0.001 });
        Assert.True(rate.AdmitRoleList());
        Assert.True(rate.AdmitRoleList());
        Assert.True(rate.AdmitRoleList());
        Assert.False(rate.AdmitRoleList());
    }
}
