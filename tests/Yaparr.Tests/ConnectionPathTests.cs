using Yaparr.Chat;
using Yaparr.Discovery;

namespace Yaparr.Tests;

public class ConnectionPathTests
{
    private static void Isolate(ChatNode x, ChatNode y)
    {
        x.Mux.OutboundFilter = (_, to) => to.Port != y.LocalPort;
        y.Mux.OutboundFilter = (_, to) => to.Port != x.LocalPort;
    }

    [Theory]
    [InlineData(0, 0, 0, "nobody")]
    [InlineData(3, 0, 0, "all directly")]
    [InlineData(2, 1, 0, "2 directly, 1 through other members")]
    [InlineData(6, 2, 1, "6 directly, 2 through other members, 1 through the shared server")]
    [InlineData(0, 0, 2, "2 through the shared server")]
    public void The_paths_are_described_in_words(int direct, int member, int server, string expected) =>
        Assert.Equal(expected, new ConnectionPaths(direct, member, server, 0, 0, 0, 0, 0).Describe());

    [Fact]
    public async Task A_direct_connection_is_counted_as_direct_and_the_check_says_so()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        var ra = await a.JoinRoomAsync("paths");
        await b.JoinRoomAsync("paths");
        await TestUtil.WaitFor(() => ra.Members.Count == 1);

        var paths = a.GetConnectionPaths();
        Assert.Equal(1, paths.Direct);
        Assert.Equal(0, paths.ViaMember);
        Assert.Equal(0, paths.ViaServer);
        Assert.True(paths.MadeDirect >= 1);
        Assert.Equal(0, paths.MadeViaMember + paths.MadeViaServer + paths.MovedOffServer);

        var report = await a.CheckNetworkAsync();
        Assert.Contains(report.Items, i => i.Title == "Connected to 1 person" && i.Detail == "All directly.");
        Assert.Contains(report.Items, i => i.Title == "Connections since Yaparr started" && i.Detail.StartsWith($"{paths.MadeDirect} direct, 0 through members, 0 through the shared server"));
        Assert.DoesNotContain(report.Items, i => i.Title == "Some connections use the shared server");
    }

    [Fact]
    public async Task A_connection_through_a_member_is_counted_as_through_a_member()
    {
        var hub = new InMemoryDiscoveryHub();
        using var relay = TestUtil.NewNode(hub, "relay");
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);
        await relay.JoinRoomAsync("paths");
        await a.JoinRoomAsync("paths");
        await b.JoinRoomAsync("paths");
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: true } && a.Network.Get(relay.Id) != null);

        var paths = a.GetConnectionPaths();
        Assert.Equal(1, paths.Direct);   // the relay
        Assert.Equal(1, paths.ViaMember); // b
        Assert.True(paths.MadeViaMember >= 1);

        var report = await a.CheckNetworkAsync();
        Assert.Contains(report.Items, i => i.Title == "Connected to 2 people" && i.Detail == "1 directly, 1 through other members.");
    }

    [Fact]
    public async Task A_connection_on_the_shared_server_is_counted_and_the_check_warns_about_its_bandwidth()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        var ra = await a.JoinRoomAsync("paths");
        await b.JoinRoomAsync("paths");
        await TestUtil.WaitFor(() => ra.Members.Count == 1);

        a.OnSharedServerOverride = c => c.RemoteId == b.Id;
        var paths = a.GetConnectionPaths();
        Assert.Equal(0, paths.Direct);
        Assert.Equal(1, paths.ViaServer);

        var report = await a.CheckNetworkAsync();
        var item = Assert.Single(report.Items, i => i.Title == "Some connections use the shared server");
        Assert.Equal(CheckLevel.Info, item.Level);
        Assert.Contains("1 of 1 connections", item.Detail);
    }

    [Fact]
    public async Task A_connection_through_a_member_that_replaces_one_on_the_server_is_counted_as_moved_off_it()
    {
        var hub = new InMemoryDiscoveryHub();
        using var relay = TestUtil.NewNode(hub, "relay");
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);
        await relay.JoinRoomAsync("paths");
        await a.JoinRoomAsync("paths");
        await b.JoinRoomAsync("paths");
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: true } && a.Network.Get(relay.Id) != null);
        var viaMember = a.Network.Get(b.Id)!;
        var straight = a.Network.Get(relay.Id)!;
        var before = a.GetConnectionPaths();

        // Replacing a direct connection, or one that is not on the server, is not a move off the server.
        a.CountConnectionMade(viaMember, straight);
        Assert.Equal(before.MovedOffServer, a.GetConnectionPaths().MovedOffServer);

        a.OnSharedServerOverride = c => c == straight;
        a.CountConnectionMade(viaMember, straight);
        var after = a.GetConnectionPaths();
        Assert.Equal(before.MovedOffServer + 1, after.MovedOffServer);
        Assert.Equal(before.MadeViaMember + 2, after.MadeViaMember);

        var report = await a.CheckNetworkAsync();
        Assert.Contains(report.Items, i => i.Title == "Some connections use the shared server" && i.Detail.Contains("1 were moved onto a member"));
    }
}
