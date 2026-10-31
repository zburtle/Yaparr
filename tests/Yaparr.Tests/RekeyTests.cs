using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class RekeyTests
{
    [Fact]
    public void A_rotated_key_is_the_same_room_under_a_new_secret_and_survives_being_written_down()
    {
        var owner = PeerIdentity.Generate().AccountId;
        var first = RoomKey.Generate(owner);
        var second = first.Rotated(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 1);
        var third = second.Rotated(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 2);

        Assert.Equal(first.RoomId, second.RoomId);
        Assert.Equal(first.RoomId, third.RoomId);
        Assert.NotEqual(first.Topic, second.Topic);
        Assert.NotEqual(second.Topic, third.Topic);
        Assert.Equal(owner, third.Owner);

        var parsed = RoomKey.Parse(third.ToString());
        Assert.Equal(third.Topic, parsed.Topic);
        Assert.Equal(2, parsed.Epoch);
        Assert.Equal(first.RoomId, parsed.RoomId);
        Assert.Equal(third.ToString(), parsed.ToString());

        // Nothing about earlier invites changed.
        Assert.Equal(first.Topic, RoomKey.Parse(first.ToString()).Topic);
        Assert.Equal(0, RoomKey.Parse(first.ToString()).Epoch);
        Assert.Throws<InvalidOperationException>(() => RoomKey.Generate().Rotated(new byte[32], 1));
    }

    [Fact]
    public void A_replacement_order_is_good_only_from_the_owner_with_its_list_and_secret_intact()
    {
        var owner = PeerIdentity.Generate();
        var other = PeerIdentity.Generate();
        var friend = PeerIdentity.Generate().AccountId.Value;
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var order = SignedRekey.Create(owner, "room", secret, 1, new[] { friend }, now);
        Assert.True(SignedRekey.Verify(order, "room", owner.AccountId));
        Assert.False(SignedRekey.Verify(order, "another room", owner.AccountId));
        Assert.False(SignedRekey.Verify(order with { Epoch = 2 }, "room", owner.AccountId));
        Assert.False(SignedRekey.Verify(order with { To = new[] { other.AccountId.Value } }, "room", owner.AccountId));
        Assert.False(SignedRekey.Verify(order with { Secret = Convert.ToBase64String(new byte[32]) }, "room", owner.AccountId));
        Assert.False(SignedRekey.Verify(SignedRekey.Create(other, "room", secret, 1, new[] { friend }, now), "room", owner.AccountId));
        Assert.False(SignedRekey.Verify(SignedRekey.Create(owner, "room", secret, 0, new[] { friend }, now), "room", owner.AccountId));
    }

    private sealed record Group(ChatNode Owner, ChatNode Bob, ChatNode Carol, ChatRoom Ro, ChatRoom Rb, ChatRoom Rc, InMemoryDiscoveryHub Hub, string OldInvite);

    private static async Task<Group> Start()
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
        return new Group(a, b, c, ra, rb, rc, hub, key.ToString());
    }

    private static List<string> Collect(ChatRoom room)
    {
        var texts = new List<string>();
        room.MessageReceived += m => { lock (texts) texts.Add(m.Text); };
        return texts;
    }

    private static bool Has(List<string> texts, string text) { lock (texts) return texts.Contains(text); }

    [Fact]
    public async Task A_banned_member_is_locked_out_by_a_new_secret_while_everyone_else_carries_on_and_newcomers_join_with_the_new_invite()
    {
        var g = await Start();
        using var _ = g.Owner; using var __ = g.Bob; using var ___ = g.Carol;
        var atOwner = Collect(g.Ro); var atBob = Collect(g.Rb); var atCarol = Collect(g.Rc);

        await g.Rb.SendAsync("before everything");
        await TestUtil.WaitFor(() => Has(atOwner, "before everything") && Has(atCarol, "before everything"), 20_000);

        g.Ro.Ban(g.Carol.AccountId, "carol");
        await TestUtil.WaitFor(() => g.Rb.IsBanned(g.Carol.AccountId), 20_000);

        // Only bob is given the new key; carol is banned and is not on the list.
        var candidates = g.Ro.RotationCandidates;
        Assert.Contains(candidates, p => p.Account == g.Bob.AccountId);
        Assert.DoesNotContain(candidates, p => p.Account == g.Carol.AccountId);
        var invite = g.Ro.RotateSecret(candidates.Select(p => p.Account).ToList());
        Assert.Equal(1, g.Ro.Epoch);
        Assert.NotEqual(g.OldInvite, invite);

        await TestUtil.WaitFor(() => g.Rb.Epoch == 1, 20_000);
        Assert.Equal(invite, g.Rb.Invite);
        Assert.Equal(g.Ro.RoomId, g.Rb.RoomId);
        Assert.Equal(0, g.Rc.Epoch);

        // They find each other again under the new key and carry on, with the history and the bans intact.
        await TestUtil.WaitFor(() => g.Ro.Members.Any(m => m.Account == g.Bob.AccountId) && g.Rb.Members.Any(m => m.Account == g.Owner.AccountId), 30_000);
        await g.Ro.SendAsync("after the new key");
        await g.Rb.SendAsync("bob after the new key");
        try { await TestUtil.WaitFor(() => Has(atBob, "after the new key") && Has(atOwner, "bob after the new key"), 30_000); }
        catch (TimeoutException)
        {
            string Texts(List<string> l) { lock (l) return string.Join("|", l); }
            string Hist(ChatRoom r) => string.Join("|", r.GetHistory().Select(m => m.Text));
            throw new TimeoutException($"bob heard [{Texts(atBob)}] owner heard [{Texts(atOwner)}]; owner history [{Hist(g.Ro)}] bob history [{Hist(g.Rb)}]; "
                + $"owner sees {g.Ro.Members.Count} member(s) at epoch {g.Ro.Epoch}, bob sees {g.Rb.Members.Count} at epoch {g.Rb.Epoch}");
        }
        Assert.True(g.Rb.IsBanned(g.Carol.AccountId));

        // Carol still has the old key: she hears nothing and nobody hears her.
        await g.Rc.SendAsync("carol after the new key");
        await Task.Delay(1500);
        Assert.False(Has(atOwner, "carol after the new key"));
        Assert.False(Has(atCarol, "after the new key"));
        Assert.False(Has(atCarol, "bob after the new key"));

        // Someone who comes with the new invite is in, and gets what was said before.
        var dave = TestUtil.NewNode(g.Hub, "dave");
        using var ____ = dave;
        var rd = await dave.JoinRoomAsync(RoomKey.Parse(invite), "lobby");
        Assert.Equal(g.Ro.RoomId, rd.RoomId);
        await TestUtil.WaitFor(() => rd.GetHistory().Any(m => m.Text == "before everything") && rd.GetHistory().Any(m => m.Text == "after the new key"), 30_000);
        Assert.True(rd.IsBanned(g.Carol.AccountId));

        // So is someone who comes with the old invite under a new name: nobody is there to meet.
        var mallory = TestUtil.NewNode(g.Hub, "mallory");
        using var _____ = mallory;
        var rm = await mallory.JoinRoomAsync(RoomKey.Parse(g.OldInvite), "lobby");
        await Task.Delay(2000);
        Assert.DoesNotContain(rm.GetHistory(), m => m.Text == "after the new key");
        Assert.DoesNotContain(g.Ro.Members, m => m.Account == mallory.AccountId);
    }

    [Fact]
    public async Task Only_the_owner_can_replace_the_secret_and_only_a_newer_order_is_taken()
    {
        var g = await Start();
        using var _ = g.Owner; using var __ = g.Bob; using var ___ = g.Carol;
        Assert.Throws<InvalidOperationException>(() => g.Rb.RotateSecret(new[] { g.Carol.AccountId }));

        g.Ro.SetAdmin(g.Bob.AccountId, true);
        await TestUtil.WaitFor(() => g.Rb.MyRole == RoomRole.Admin, 20_000);
        Assert.Throws<InvalidOperationException>(() => g.Rb.RotateSecret(new[] { g.Carol.AccountId }));

        var first = g.Ro.RotateSecret(g.Ro.RotationCandidates.Select(p => p.Account).ToList());
        await TestUtil.WaitFor(() => g.Rb.Epoch == 1 && g.Rc.Epoch == 1, 20_000);
        Assert.Equal(first, g.Rc.Invite);

        await TestUtil.WaitFor(() => g.Ro.Members.Count >= 2 && g.Rb.Members.Count >= 2 && g.Rc.Members.Count >= 2, 30_000);
        var second = g.Ro.RotateSecret(g.Ro.RotationCandidates.Select(p => p.Account).ToList());
        Assert.Equal(2, g.Ro.Epoch);
        await TestUtil.WaitFor(() => g.Rb.Epoch == 2 && g.Rc.Epoch == 2, 20_000);
        Assert.Equal(second, g.Rb.Invite);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task A_newer_invite_for_a_room_we_are_in_is_taken_so_other_devices_follow()
    {
        var g = await Start();
        using var _ = g.Owner; using var __ = g.Bob; using var ___ = g.Carol;
        var newer = RoomKey.Parse(g.Ro.RotateSecret(Array.Empty<PeerId>()));
        Assert.Equal(1, newer.Epoch);

        // Carol is not on the list, but is handed the new invite some other way.
        Assert.Equal(0, g.Rc.Epoch);
        var again = await g.Carol.JoinRoomAsync(newer, "lobby");
        Assert.Same(g.Rc, again);
        Assert.Equal(1, g.Rc.Epoch);
        Assert.Equal(newer.ToString(), g.Rc.Invite);

        // An older invite does not take the room back.
        await g.Carol.JoinRoomAsync(RoomKey.Parse(g.OldInvite), "lobby");
        Assert.Equal(1, g.Rc.Epoch);
    }

    [Fact]
    public async Task A_room_without_an_owner_cannot_replace_its_secret()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "alice");
        var room = await a.JoinRoomAsync("old room");
        Assert.Throws<InvalidOperationException>(() => room.RotateSecret(Array.Empty<PeerId>()));
    }

    [Fact]
    public async Task A_listed_member_who_was_away_is_given_the_new_key_when_they_come_back()
    {
        var hub = new InMemoryDiscoveryHub();
        var owner = TestUtil.NewNode(hub, "owner");
        var bob = TestUtil.NewNode(hub, "bob");
        var carolId = PeerIdentity.Generate();
        var carol = TestUtil.NewNode(hub, "carol", identity: carolId);
        using var _ = owner; using var __ = bob;
        var key = RoomKey.Generate(owner.AccountId);
        var oldInvite = key.ToString();
        var ro = await owner.JoinRoomAsync(key, "lobby");
        var rb = await bob.JoinRoomAsync(RoomKey.Parse(oldInvite), "lobby");
        var rc = await carol.JoinRoomAsync(RoomKey.Parse(oldInvite), "lobby");
        await TestUtil.WaitFor(() => ro.Members.Count >= 2 && rb.Members.Count >= 2 && rc.Members.Count >= 2, 30_000);

        // Carol goes away, and the key is replaced while she is gone.
        carol.Dispose();
        var candidates = ro.RotationCandidates;
        Assert.Contains(candidates, c => c.Account == carolId.AccountId);
        var invite = ro.RotateSecret(candidates.Select(c => c.Account).ToList());
        await TestUtil.WaitFor(() => rb.Epoch == 1, 20_000);

        // She comes back still holding the old invite, and is handed the new key by whoever she meets.
        using var back = TestUtil.NewNode(hub, "carol", identity: carolId);
        var rcBack = await back.JoinRoomAsync(RoomKey.Parse(oldInvite), "lobby");
        Assert.Equal(0, rcBack.Epoch);
        await TestUtil.WaitFor(() => rcBack.Epoch == 1, 60_000);
        Assert.Equal(invite, rcBack.Invite);

        var atOwner = Collect(ro);
        await TestUtil.WaitFor(() => ro.Members.Any(m => m.Account == carolId.AccountId) && rcBack.Members.Any(m => m.Account == owner.AccountId), 30_000);
        await rcBack.SendAsync("I am back");
        await TestUtil.WaitFor(() => Has(atOwner, "I am back"), 30_000);
    }

    [Fact]
    public async Task Someone_who_is_not_on_the_list_is_given_nothing_however_long_they_wait_and_whatever_old_key_they_hold()
    {
        var g = await Start();
        using var _ = g.Owner; using var __ = g.Bob; using var ___ = g.Carol;
        g.Ro.Ban(g.Carol.AccountId, "carol");
        await TestUtil.WaitFor(() => g.Rb.IsBanned(g.Carol.AccountId), 20_000);
        g.Ro.RotateSecret(g.Ro.RotationCandidates.Select(c => c.Account).ToList());
        await TestUtil.WaitFor(() => g.Rb.Epoch == 1, 20_000);

        // A stranger with the old invite, who was never seen in the room, finds the old place and is given nothing.
        var mallory = TestUtil.NewNode(g.Hub, "mallory");
        using var ____ = mallory;
        var rm = await mallory.JoinRoomAsync(RoomKey.Parse(g.OldInvite), "lobby");
        await Task.Delay(6000);
        Assert.Equal(0, rm.Epoch);
        Assert.Equal(0, g.Rc.Epoch);
    }

    [Fact]
    public async Task The_earlier_keys_and_who_was_in_the_room_survive_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yaparr-rekey-" + Guid.NewGuid().ToString("N"));
        try
        {
            var hub = new InMemoryDiscoveryHub();
            var identity = PeerIdentity.Generate();
            var key = RoomKey.Generate(identity.AccountId);
            var friend = TestUtil.NewNode(hub, "friend");
            using var _ = friend;
            ChatNodeOptions Options() => new()
            {
                Identity = identity, RolesDirectory = dir, DisplayName = "owner",
                DiscoveryFactory = mux => hub.CreateClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, mux.LocalPort)),
                DiscoveryInterval = TimeSpan.FromMilliseconds(300), Network = TestUtil.FastNetwork,
            };
            string invite;
            using (var first = new ChatNode(Options()))
            {
                var room = await first.JoinRoomAsync(key, "lobby");
                var rf = await friend.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
                await TestUtil.WaitFor(() => room.Members.Count >= 1, 30_000);
                invite = room.RotateSecret(room.RotationCandidates.Select(c => c.Account).ToList());
                await Task.Delay(200);
            }
            using var second = new ChatNode(Options());
            var again = await second.JoinRoomAsync(RoomKey.Parse(invite), "lobby");
            Assert.Equal(1, again.Epoch);
            Assert.Single(again.PreviousKeys);
            Assert.Equal(key.ToString(), again.PreviousKeys[0].Key.ToString());
            Assert.NotNull(again.LastRekey);
            Assert.Contains(again.RotationCandidates, c => c.Account == friend.AccountId);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
