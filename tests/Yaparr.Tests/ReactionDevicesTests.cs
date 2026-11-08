using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class ReactionDevicesTests
{
    private const string Thumbs = "\U0001F44D";

    [Fact]
    public async Task A_room_reaction_put_on_while_the_other_device_was_off_is_that_devices_own_when_it_returns()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        using var desktop = TestUtil.NewNode(hub, "zach", identity: firstId);
        using var friend = TestUtil.NewNode(hub, "bob");
        var rd = await desktop.JoinRoomAsync("lobby");
        var rf = await friend.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rd.Members.Count == 1 && rf.Members.Count == 1);
        await rf.SendAsync("hello");
        await TestUtil.WaitFor(() => rd.GetHistory().Any());
        var key = rd.GetHistory().Single().Key;
        await rd.SendReactionAsync(key, Thumbs, on: true);

        using var phone = TestUtil.NewNode(hub, "zach", identity: secondId);
        var rp = await phone.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rp.GetReactions().For(key).Count == 1);
        // Whatever the phone has learnt about the computer by now, it is the same person: one reaction, and it is the phone's own.
        await TestUtil.WaitFor(() => rp.GetReactions().Has(key, Thumbs, phone.AccountId), 3000);
        Assert.Equal(1, rp.GetReactions().For(key).Single().Count);
    }

    [Fact]
    public async Task A_direct_reaction_from_one_device_is_the_accounts_on_the_other_and_taking_it_off_works()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        using var desktop = TestUtil.NewNode(hub, "zach", identity: firstId);
        using var phone = TestUtil.NewNode(hub, "zach", identity: secondId);
        using var friend = TestUtil.NewNode(hub, "bob");
        var rd = await desktop.JoinRoomAsync("lobby");
        var rp = await phone.JoinRoomAsync("lobby");
        var rf = await friend.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rd.Members.Count == 2 && rp.Members.Count == 2 && rf.Members.Count == 2);

        var fromFriend = await friend.OpenDirectChatAsync(desktop.AccountId);
        await fromFriend.SendAsync("hi");
        var dd = await desktop.OpenDirectChatAsync(friend.AccountId);
        var dp = await phone.OpenDirectChatAsync(friend.AccountId);
        await TestUtil.WaitFor(() => dd.History.Any() && dp.History.Any());
        var key = dd.History.Single().Key;

        await dd.SendReactionAsync(key, Thumbs, on: true);
        await TestUtil.WaitFor(() => dp.GetReactions().For(key).Count == 1);
        Assert.True(dp.GetReactions().Has(key, Thumbs, phone.AccountId), "the phone should see the computer's reaction as its own account's");
        Assert.Equal(1, dp.GetReactions().For(key).Single().Count);

        await dp.SendReactionAsync(key, Thumbs, on: false);
        Assert.Empty(dp.GetReactions().For(key));
        await TestUtil.WaitFor(() => dd.GetReactions().For(key).Count == 0);
    }

    [Fact]
    public async Task A_reaction_from_a_device_that_has_since_gone_is_still_the_accounts_when_the_other_device_comes_on()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        var desktop = TestUtil.NewNode(hub, "zach", identity: firstId);
        using var friend = TestUtil.NewNode(hub, "bob");
        var rd = await desktop.JoinRoomAsync("lobby");
        var rf = await friend.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rd.Members.Count == 1 && rf.Members.Count == 1);
        await rf.SendAsync("hello");
        await TestUtil.WaitFor(() => rd.GetHistory().Any());
        var key = rd.GetHistory().Single().Key;
        await rd.SendReactionAsync(key, Thumbs, on: true);
        await TestUtil.WaitFor(() => rf.GetReactions().For(key).Count == 1);
        desktop.Dispose();

        using var phone = TestUtil.NewNode(hub, "zach", identity: secondId);
        var rp = await phone.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rp.GetReactions().For(key).Count == 1);
        Assert.True(rp.GetReactions().Has(key, Thumbs, phone.AccountId), "the computer's reaction should be the account's, so the phone's own");
        await rp.SendReactionAsync(key, Thumbs, on: true);
        Assert.Equal(1, rp.GetReactions().For(key).Single().Count);
    }

    [Fact]
    public async Task A_reaction_from_a_linked_device_that_has_since_gone_is_still_the_accounts_when_the_first_device_comes_on()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        var phone = TestUtil.NewNode(hub, "zach", identity: secondId);
        using var friend = TestUtil.NewNode(hub, "bob");
        var rp = await phone.JoinRoomAsync("lobby");
        var rf = await friend.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rp.Members.Count == 1 && rf.Members.Count == 1);
        await rf.SendAsync("hello");
        await TestUtil.WaitFor(() => rp.GetHistory().Any());
        var key = rp.GetHistory().Single().Key;
        await rp.SendReactionAsync(key, Thumbs, on: true);
        await TestUtil.WaitFor(() => rf.GetReactions().For(key).Count == 1);
        phone.Dispose();

        using var desktop = TestUtil.NewNode(hub, "zach", identity: firstId);
        var rd = await desktop.JoinRoomAsync("lobby");
        await TestUtil.WaitFor(() => rd.GetReactions().For(key).Count == 1);
        Assert.True(rd.GetReactions().Has(key, Thumbs, desktop.AccountId), "the phone's reaction should be the account's, so the computer's own");
        await rd.SendReactionAsync(key, Thumbs, on: true);
        Assert.Equal(1, rd.GetReactions().For(key).Single().Count);
    }
}
