using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class SiblingDiscoveryTests
{
    [Fact]
    public async Task Devices_of_one_account_find_each_other_without_a_room_in_common()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        using var first = TestUtil.NewNode(hub, "alice", identity: firstId);
        using var second = TestUtil.NewNode(hub, "alice", identity: secondId);
        first.Start();
        second.Start();

        await TestUtil.WaitFor(() => first.ConnectedPeers.Any(p => p.Id == second.Id) && second.ConnectedPeers.Any(p => p.Id == first.Id));
        Assert.True(first.IsMyDevice(second.Id));
        Assert.True(second.IsMyDevice(first.Id));
    }

    [Fact]
    public async Task A_device_with_no_rooms_receives_the_rooms_of_the_one_it_is_linked_to()
    {
        // What a phone linked with a QR code is: the same account, and no rooms yet.
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        using var first = TestUtil.NewNode(hub, "alice", identity: firstId);
        await first.JoinRoomAsync("lobby");

        using var second = TestUtil.NewNode(hub, "alice", identity: secondId);
        var joined = new TaskCompletionSource<SyncedRoom>();
        second.SyncedRoomChanged += (room, isJoined) => { if (isJoined) joined.TrySetResult(room); };
        second.Start();

        var room = await joined.Task.WaitAsync(TimeSpan.FromSeconds(15 * TestUtil.Patience));
        Assert.Equal("lobby", room.Name);
    }

    [Fact]
    public async Task A_device_of_another_account_is_not_connected_to()
    {
        var hub = new InMemoryDiscoveryHub();
        using var alice = TestUtil.NewNode(hub, "alice");
        using var carol = TestUtil.NewNode(hub, "carol");
        alice.Start();
        carol.Start();

        await Task.Delay(1500);
        Assert.Empty(alice.ConnectedPeers);
        Assert.Empty(carol.ConnectedPeers);
    }
}
