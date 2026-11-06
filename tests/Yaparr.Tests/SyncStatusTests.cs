using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public class SyncStatusTests
{
    [Fact]
    public void Banner_counts_rooms_that_are_done_and_ignores_quiet_ones()
    {
        var text = SyncStatus.Banner(DeviceSearchState.None,
            new[] { RoomSyncState.Ready, RoomSyncState.Ready, RoomSyncState.LoadingHistory, RoomSyncState.FindingMembers, RoomSyncState.NoOneOnline }, out var progress);
        Assert.Equal("Syncing 2 of 4 rooms…", text);
        Assert.Equal(0.5, progress);
    }

    [Fact]
    public void Banner_is_empty_when_everything_is_ready_or_quiet()
    {
        Assert.Null(SyncStatus.Banner(DeviceSearchState.None, new[] { RoomSyncState.Ready, RoomSyncState.NoOneOnline }, out var progress));
        Assert.Null(progress);
    }

    [Fact]
    public void Looking_for_the_other_device_comes_before_the_rooms()
    {
        Assert.Equal("Looking for your other device…", SyncStatus.Banner(DeviceSearchState.Looking, Array.Empty<RoomSyncState>(), out _));
        Assert.Contains("Can't reach", SyncStatus.Banner(DeviceSearchState.NotFound, Array.Empty<RoomSyncState>(), out _));
    }

    [Fact]
    public void A_room_with_no_one_in_it_does_not_look_busy()
    {
        Assert.False(SyncStatus.IsBusy(RoomSyncState.NoOneOnline));
        Assert.True(SyncStatus.IsBusy(RoomSyncState.FindingMembers));
        Assert.Equal("", SyncStatus.RoomText(RoomSyncState.Ready));
    }

    [Fact]
    public async Task A_new_room_with_no_members_is_finding_them()
    {
        using var node = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice");
        node.Start();
        var room = await node.JoinRoomAsync(RoomKey.Generate());
        Assert.Equal(RoomSyncState.FindingMembers, room.SyncState);
    }

    [Fact]
    public async Task A_linked_device_looks_for_the_other_one_and_then_has_it()
    {
        var hub = new InMemoryDiscoveryHub();
        using var firstId = PeerIdentity.Generate();
        using var secondId = firstId.CreateDevice();
        using var first = TestUtil.NewNode(hub, "alice", identity: firstId);
        using var second = TestUtil.NewNode(hub, "alice", identity: secondId);
        var search = new DeviceSearch();
        Assert.Equal(DeviceSearchState.None, search.Update(second));
        search.Begin();
        Assert.Equal(DeviceSearchState.Looking, search.Update(second));
        first.Start();
        second.Start();
        await TestUtil.WaitFor(() => second.HasOwnDeviceConnected);
        Assert.Equal(DeviceSearchState.Connected, search.Update(second));
    }
}
