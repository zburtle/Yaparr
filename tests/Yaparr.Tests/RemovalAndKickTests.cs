using System.Collections.Concurrent;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

/// <summary>The owner removes someone from the room, or kicks them from voice, without banning them.</summary>
[Collection("real-time audio")]
public class RemovalAndKickTests
{
    private sealed record Room(ChatNode Owner, ChatNode B, ChatNode C, ChatRoom Ro, ChatRoom Rb, ChatRoom Rc, InMemoryDiscoveryHub Hub, RoomKey Key);

    private static async Task<Room> Trio(PeerIdentity? carol = null)
    {
        var hub = new InMemoryDiscoveryHub();
        var a = TestUtil.NewNode(hub, "owner");
        var b = TestUtil.NewNode(hub, "bob");
        var c = TestUtil.NewNode(hub, "carol", identity: carol);
        var key = RoomKey.Generate(a.AccountId);
        var ra = await a.JoinRoomAsync(key, "lobby");
        var rb = await b.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        var rc = await c.JoinRoomAsync(RoomKey.Parse(key.ToString()), "lobby");
        await TestUtil.WaitFor(() => ra.Members.Count >= 2 && rb.Members.Count >= 2 && rc.Members.Count >= 2, 30_000);
        return new Room(a, b, c, ra, rb, rc, hub, key);
    }

    [Fact]
    public void The_two_orders_are_signed_like_the_others_and_need_an_account_id_for_a_target()
    {
        var owner = PeerIdentity.Generate();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var target = PeerIdentity.Generate().AccountId.Value;
        foreach (var kind in new[] { SignedMod.RemoveFromRoom, SignedMod.KickFromVoice })
        {
            var order = SignedMod.Create(owner, "room", kind, target, "carol", now);
            Assert.True(SignedMod.Verify(order, "room", out var signer));
            Assert.Equal(owner.AccountId, signer);
            Assert.False(SignedMod.Verify(order, "another room", out _));
            Assert.False(SignedMod.Verify(order with { Target = "abc" }, "room", out _));   // not an account id
            Assert.False(SignedMod.Verify(order with { Ts = order.Ts + 1 }, "room", out _));
        }
    }

    [Fact]
    public async Task Only_the_owner_can_remove_someone_or_kick_them_from_voice_and_not_themselves()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;

        // A member cannot; an admin cannot either (these are the owner's alone).
        Assert.Throws<InvalidOperationException>(() => t.Rb.RemoveFromRoom(t.C.AccountId, "carol"));
        Assert.Throws<InvalidOperationException>(() => t.Rb.KickFromVoice(t.C.AccountId));
        t.Ro.SetAdmin(t.B.AccountId, true);
        await TestUtil.WaitFor(() => t.Rb.MyRole == RoomRole.Admin, 20_000);
        Assert.Throws<InvalidOperationException>(() => t.Rb.RemoveFromRoom(t.C.AccountId, "carol"));
        Assert.Throws<InvalidOperationException>(() => t.Rb.KickFromVoice(t.C.AccountId));

        // Not yourself; and nobody can be kicked from voice who is not in it.
        Assert.Throws<InvalidOperationException>(() => t.Ro.RemoveFromRoom(t.Owner.AccountId, "me"));
        Assert.Throws<InvalidOperationException>(() => t.Ro.KickFromVoice(t.Owner.AccountId));
        var notInVoice = Assert.Throws<InvalidOperationException>(() => t.Ro.KickFromVoice(t.C.AccountId));
        Assert.Contains("not in a voice channel", notInVoice.Message);
        Assert.Throws<ArgumentException>(() => t.Ro.RemoveFromRoom(new PeerId("xyz"), "nobody"));
    }

    [Fact]
    public async Task A_removed_member_is_told_and_leaves_without_being_banned_and_can_come_back()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        var removedAtCarol = new TaskCompletionSource();
        t.Rc.RemovedFromRoom += () => removedAtCarol.TrySetResult();
        var aboutBob = new ConcurrentQueue<string>();
        t.Rb.RemovedFromRoom += () => aboutBob.Enqueue("bob was told");

        t.Ro.RemoveFromRoom(t.C.AccountId, "carol");
        await removedAtCarol.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(aboutBob);                                  // only the person it is about
        Assert.False(t.Rb.IsBanned(t.C.AccountId));              // not a ban
        Assert.Empty(t.Rb.Banned);

        // Her app leaves the room, as the app does when told.
        await t.Rc.LeaveAsync();
        await TestUtil.WaitFor(() => t.Rb.People.All(p => p.Account != t.C.AccountId) && t.Ro.People.All(p => p.Account != t.C.AccountId), 30_000);

        // She comes back by opening the invite again: there is nothing to keep her out, she is listed again, and the order that is waiting does not remove her again.
        var removedAgain = new ConcurrentQueue<string>();
        var back = await t.C.JoinRoomAsync(RoomKey.Parse(t.Key.ToString()), "lobby");
        back.RemovedFromRoom += () => removedAgain.Enqueue("again");
        await TestUtil.WaitFor(() => t.Rb.Members.Any(m => m.Account == t.C.AccountId) && t.Rb.People.Any(p => p.Account == t.C.AccountId && p.Online), 30_000);
        await Task.Delay(3000);
        Assert.Empty(removedAgain);
        Assert.False(t.Rb.IsBanned(t.C.AccountId));
    }

    [Fact]
    public async Task Someone_who_is_off_when_they_are_removed_is_gone_from_the_lists_and_back_when_they_come_on()
    {
        using var carolKey = PeerIdentity.Generate();
        var t = await Trio(carolKey);
        using var owner = t.Owner; using var bob = t.B;
        var carolId = t.C.AccountId;
        // Everyone has seen carol; then she goes off.
        await TestUtil.WaitFor(() => t.Rb.Members.Any(m => m.Account == carolId), 20_000);
        Assert.Contains(t.Rb.People, p => p.Account == carolId);    // her name is noted
        t.C.Dispose();
        await TestUtil.WaitFor(() => t.Rb.People.Any(p => p.Account == carolId && !p.Online), 30_000);
        await TestUtil.WaitFor(() => t.Ro.People.Any(p => p.Account == carolId && !p.Online), 30_000);

        t.Ro.RemoveFromRoom(carolId, "carol");
        await TestUtil.WaitFor(() => t.Rb.People.All(p => p.Account != carolId) && t.Ro.People.All(p => p.Account != carolId), 30_000);
        Assert.DoesNotContain(t.Rb.RotationCandidates, c => c.Account == carolId);   // nor is she handed a new secret

        // She comes on later with her own identity: she is a member again, and so in the lists.
        using var again = TestUtil.NewNode(t.Hub, "carol", identity: carolKey);
        var back = await again.JoinRoomAsync(RoomKey.Parse(t.Key.ToString()), "lobby");
        await TestUtil.WaitFor(() => t.Rb.People.Any(p => p.Account == carolId && p.Online) && t.Ro.People.Any(p => p.Account == carolId && p.Online), 40_000);
        Assert.False(t.Rb.IsBanned(carolId));
    }

    [Fact]
    public async Task A_member_kicked_from_voice_leaves_the_call_without_being_banned_and_can_join_again()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        using var vb = t.Rb.JoinVoice();
        using var vc = t.Rc.JoinVoice();
        await TestUtil.WaitFor(() => t.Rb.VoiceParticipants.Count == 2 && t.Ro.VoiceParticipants.Any(p => p.Peer.Account == t.C.AccountId), 30_000);

        var kickedAtCarol = new TaskCompletionSource();
        t.Rc.VoiceKicked += () => kickedAtCarol.TrySetResult();
        var aboutBob = new ConcurrentQueue<string>();
        t.Rb.VoiceKicked += () => aboutBob.Enqueue("bob was kicked");

        t.Ro.KickFromVoice(t.C.AccountId);
        await kickedAtCarol.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(t.Rc.ActiveVoice);                            // her call here has ended
        Assert.Empty(aboutBob);
        await TestUtil.WaitFor(() => t.Rb.VoiceParticipants.All(p => p.Peer.Account != t.C.AccountId) && t.Ro.VoiceParticipants.All(p => p.Peer.Account != t.C.AccountId), 20_000);
        Assert.False(t.Rb.IsBanned(t.C.AccountId));               // not a ban: she is still in the room
        Assert.Contains(t.Rb.Members, m => m.Account == t.C.AccountId);
        Assert.NotNull(t.Rb.ActiveVoice);                         // and bob's call goes on

        // She joins voice again: nothing keeps her out, and the order that is waiting does not kick her again.
        var again = new ConcurrentQueue<string>();
        t.Rc.VoiceKicked += () => again.Enqueue("kicked again");
        await Task.Delay(500);
        using var back = t.Rc.JoinVoice();
        await TestUtil.WaitFor(() => t.Rb.VoiceParticipants.Any(p => p.Peer.Account == t.C.AccountId) && t.Ro.VoiceParticipants.Any(p => p.Peer.Account == t.C.AccountId), 30_000);
        await Task.Delay(3000);
        Assert.Empty(again);
        Assert.NotNull(t.Rc.ActiveVoice);
        Assert.Contains(t.Rb.VoiceParticipants, p => p.Peer.Account == t.C.AccountId);
    }

    [Fact]
    public async Task An_old_order_does_not_remove_or_kick_anyone_who_was_not_there_when_it_was_given()
    {
        var t = await Trio();
        using var _ = t.Owner; using var __ = t.B; using var ___ = t.C;
        // The owner gives both orders while carol is on; carol's app obeys. A fourth person who opens the room later finds the orders waiting and is not touched.
        using var vc = t.Rc.JoinVoice();
        await TestUtil.WaitFor(() => t.Ro.VoiceParticipants.Any(p => p.Peer.Account == t.C.AccountId), 30_000);
        var told = new TaskCompletionSource();
        t.Rc.VoiceKicked += () => told.TrySetResult();
        t.Ro.KickFromVoice(t.C.AccountId);
        await told.Task.WaitAsync(TimeSpan.FromSeconds(20));
        t.Ro.RemoveFromRoom(t.C.AccountId, "carol");

        using var dave = TestUtil.NewNode(t.Hub, "dave");
        var davesRoom = await dave.JoinRoomAsync(RoomKey.Parse(t.Key.ToString()), "lobby");
        var touched = new ConcurrentQueue<string>();
        davesRoom.RemovedFromRoom += () => touched.Enqueue("removed");
        davesRoom.VoiceKicked += () => touched.Enqueue("kicked");
        await TestUtil.WaitFor(() => davesRoom.Members.Count >= 2, 30_000);
        await Task.Delay(2500);
        Assert.Empty(touched);
    }
}
