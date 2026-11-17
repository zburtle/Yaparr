using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

public sealed class CloudUsersTests
{
    private static async Task<(ChatNode Alice, ChatNode Bob)> ConnectedPair(bool bobSignedInFirst = false)
    {
        var hub = new InMemoryDiscoveryHub();
        var alice = TestUtil.NewNode(hub, "alice");
        var bob = TestUtil.NewNode(hub, "bob");
        if (bobSignedInFirst) bob.MailboxEnabled = true;
        var ra = await alice.JoinRoomAsync("Home");
        await bob.JoinRoomAsync("Home");
        await TestUtil.WaitFor(() => ra.Members.Count == 1);
        return (alice, bob);
    }

    [Fact]
    public async Task A_person_who_signs_in_to_a_server_shows_a_cloud_to_the_people_they_are_connected_to_and_loses_it_when_they_sign_out()
    {
        var (alice, bob) = await ConnectedPair();
        using var _1 = alice;
        using var _2 = bob;
        Assert.False(alice.UsesCloud(bob.AccountId));
        int changes = 0;
        alice.CloudUsersChanged += () => Interlocked.Increment(ref changes);

        bob.MailboxEnabled = true;
        await TestUtil.WaitFor(() => alice.UsesCloud(bob.AccountId));
        Assert.True(bob.UsesCloud(bob.AccountId));
        await TestUtil.WaitFor(() => Volatile.Read(ref changes) > 0);   // raised just after it is recorded

        bob.MailboxEnabled = false;
        await TestUtil.WaitFor(() => !alice.UsesCloud(bob.AccountId));
        Assert.False(bob.UsesCloud(bob.AccountId));
    }

    [Fact]
    public async Task Someone_who_was_already_signed_in_when_they_connected_shows_it_from_the_start()
    {
        var (alice, bob) = await ConnectedPair(bobSignedInFirst: true);
        using var _1 = alice;
        using var _2 = bob;
        await TestUtil.WaitFor(() => alice.UsesCloud(bob.AccountId));
        Assert.False(bob.UsesCloud(alice.AccountId));
    }
}
