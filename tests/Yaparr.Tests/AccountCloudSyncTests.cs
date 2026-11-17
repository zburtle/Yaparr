using Yaparr.AppCore;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

public sealed class AccountCloudSyncTests
{
    private static readonly SyncSealer Sealer = SyncSealer.FromRecoveryCode("K7QD-M3XF-9PLR-2WTE-HV8N-C4YB");

    private static AccountCloudSync For(ChatNode node, FakeSyncServer server, ISyncState? state = null, string? device = null) =>
        new(() => node.AccountEntries, node.ApplyCloudEntries, state ?? new MemorySyncState(), server, Sealer, device ?? node.Id.Value);

    // Two devices of one account that are never online together (each on a hub of its own), so only the server can carry anything between them.
    private static (ChatNode First, ChatNode Second) TwoApart()
    {
        var firstId = PeerIdentity.Generate();
        var first = TestUtil.NewNode(new InMemoryDiscoveryHub(), "alice", identity: firstId);
        var pass = DeviceLink.GeneratePassphrase();
        var linked = DeviceLink.Open(DeviceLink.Create(firstId, first.DisplayName, first.AccountEntries, pass), pass);
        var second = TestUtil.NewNode(new InMemoryDiscoveryHub(), linked.DisplayName, identity: linked.Identity, accountEntries: linked.Entries);
        return (first, second);
    }

    [Fact]
    public async Task A_block_and_a_room_made_on_one_device_reach_a_device_that_was_never_online_with_it()
    {
        var (first, second) = TwoApart();
        using var _1 = first;
        using var _2 = second;
        var server = new FakeSyncServer();
        var nuisance = PeerIdentity.Generate().Id;
        first.Block(nuisance);
        var key = RoomKey.Generate();
        await first.JoinRoomAsync(key, "book club");
        var joined = new List<(SyncedRoom Room, bool Joined)>();
        second.SyncedRoomChanged += (room, on) => joined.Add((room, on));

        Assert.Equal(AccountSyncResult.Synced, (await For(first, server).SyncAsync()).Result);
        Assert.Equal(AccountSyncResult.Synced, (await For(second, server).SyncAsync()).Result);

        Assert.True(second.IsBlocked(nuisance));
        Assert.Equal((key.ToString(), "book club", true), joined.Select(j => (j.Room.Key, j.Room.Name, j.Joined)).Single());
    }

    [Fact]
    public async Task What_the_server_holds_is_locked_and_a_room_key_is_not_in_the_clear()
    {
        var (first, second) = TwoApart();
        using var _1 = first;
        using var _2 = second;
        var server = new FakeSyncServer();
        var key = RoomKey.Generate();
        await first.JoinRoomAsync(key, "secret place");
        first.Block(PeerIdentity.Generate().Id);

        await For(first, server).SyncAsync();

        Assert.NotEmpty(server.Held);
        Assert.All(server.Held, r =>
        {
            Assert.DoesNotContain(key.ToString(), r.Body);
            Assert.DoesNotContain("secret", r.Body);
        });
        // Someone else's recovery code opens none of it.
        var stranger = new AccountCloudSync(() => second.AccountEntries, _ => Assert.Fail("nothing should open"), new MemorySyncState(), server,
            SyncSealer.FromRecoveryCode("AAAA-BBBB-CCCC-DDDD-EEEE-FFFF"), "stranger");
        var (_, report) = await stranger.SyncAsync();
        Assert.Equal(0, report!.Received);
        Assert.True(report.Unreadable > 0);
    }

    [Fact]
    public async Task A_rename_travels_and_the_newer_of_two_changes_wins_on_both()
    {
        var (first, second) = TwoApart();
        using var _1 = first;
        using var _2 = second;
        var server = new FakeSyncServer();
        var a = For(first, server);
        var b = For(second, server);

        first.Rename("Alice B.");
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("Alice B.", second.DisplayName);

        // Both rename while apart; the later change is the one both keep.
        first.Rename("Alice X");
        await Task.Delay(5);
        second.Rename("Alice Y");
        await a.SyncAsync();
        await b.SyncAsync();
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("Alice Y", first.DisplayName);
        Assert.Equal("Alice Y", second.DisplayName);
    }

    [Fact]
    public async Task Unblocking_on_one_device_unblocks_the_other_and_nothing_bounces_back_and_forth()
    {
        var (first, second) = TwoApart();
        using var _1 = first;
        using var _2 = second;
        var server = new FakeSyncServer();
        var a = For(first, server);
        var b = For(second, server);
        var nuisance = PeerIdentity.Generate().Id;
        first.Block(nuisance);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.True(second.IsBlocked(nuisance));

        await Task.Delay(5);
        second.Unblock(nuisance);
        await b.SyncAsync();
        await a.SyncAsync();
        Assert.False(first.IsBlocked(nuisance));

        // Settled: another round writes and reads nothing new.
        var before = server.Pushes;
        var (_, report) = await a.SyncAsync();
        Assert.Equal(0, report!.Sent);
        await b.SyncAsync();
        Assert.Equal(before, server.Pushes);
    }

    [Fact]
    public async Task What_is_read_in_a_chat_stays_between_the_devices_and_does_not_go_to_the_server()
    {
        var (first, second) = TwoApart();
        using var _1 = first;
        using var _2 = second;
        var server = new FakeSyncServer();
        await For(first, server).SyncAsync();
        Assert.DoesNotContain(server.Held, r => r.Id.StartsWith("read:"));
        Assert.False(ChatNode.IsCloudEntry(new AccountEntry("read:abc", "1", 1)));
    }

    [Fact]
    public void The_state_in_a_file_is_still_there_after_a_restart_with_what_was_not_yet_sent()
    {
        var path = Path.Combine(Path.GetTempPath(), "yaparr-sync-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var clock = new SyncClock("dev1");
            var state = new FileSyncState(path);
            var written = new SyncEntry("account", "block:1", clock.Next(), "dev1", false, new byte[] { 1, 2, 3 });
            var read = new SyncEntry("account", "room:2", clock.Next(), "dev2", false, new byte[] { 9 });
            state.Put(written, dirty: true);
            state.Put(read, dirty: false);
            state.Cursor = 42;

            var again = new FileSyncState(path);
            Assert.Equal(42, again.Cursor);
            Assert.Equal(new byte[] { 1, 2, 3 }, again.Get("account", "block:1")!.Value);
            Assert.Equal("block:1", Assert.Single(again.Dirty()).Id);
            Assert.Equal(2, again.All().Count);

            again.MarkSent(written);
            Assert.Empty(new FileSyncState(path).Dirty());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_state_file_that_cannot_be_read_starts_the_state_again_instead_of_failing()
    {
        var path = Path.Combine(Path.GetTempPath(), "yaparr-sync-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "this is not a state file");
            var logged = new List<string>();
            var state = new FileSyncState(path, logged.Add);
            Assert.Equal(0, state.Cursor);
            Assert.Empty(state.All());
            Assert.Single(logged);
        }
        finally { File.Delete(path); }
    }
}
