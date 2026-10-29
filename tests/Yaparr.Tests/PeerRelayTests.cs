using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Transport;

namespace Yaparr.Tests;

public class PeerRelayTests
{
    private static void Isolate(ChatNode x, ChatNode y)
    {
        x.Mux.OutboundFilter = (_, to) => to.Port != y.LocalPort;
        y.Mux.OutboundFilter = (_, to) => to.Port != x.LocalPort;
    }

    [Fact]
    public async Task Members_whose_connection_cannot_relay_are_not_used_when_one_that_can_is_there()
    {
        var hub = new InMemoryDiscoveryHub();
        using var weak = TestUtil.NewNode(hub, "weak", relayCapable: false);
        using var strong = TestUtil.NewNode(hub, "strong", relayCapable: true);
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);

        await weak.JoinRoomAsync("mesh");
        await strong.JoinRoomAsync("mesh");
        await a.JoinRoomAsync("mesh");
        await b.JoinRoomAsync("mesh");

        await TestUtil.WaitFor(() => a.Network.Get(b.Id) != null && b.Network.Get(a.Id) != null, 20_000);
        Assert.Equal(strong.Id, a.Network.Get(b.Id)!.RelayedVia);
        Assert.Equal(strong.Id, b.Network.Get(a.Id)!.RelayedVia);
        Assert.True(strong.RelayStats.ForwardedFrames > 0);
        Assert.Equal(0, weak.RelayStats.ForwardedFrames);
    }

    [Fact]
    public async Task A_member_that_cannot_relay_refuses_even_when_asked_directly()
    {
        var hub = new InMemoryDiscoveryHub();
        using var weak = TestUtil.NewNode(hub, "weak", relayCapable: false);
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);

        await weak.JoinRoomAsync("mesh");
        var ra = await a.JoinRoomAsync("mesh");
        await b.JoinRoomAsync("mesh");
        await TestUtil.WaitFor(() => a.Network.Get(weak.Id) != null && b.Network.Get(weak.Id) != null && ra.Members.Count >= 1);

        var forced = a.Network.ConnectVia(a.Network.Get(weak.Id)!, b.Id);
        await Task.Delay(2000);
        Assert.Equal(ConnectionState.Connecting, forced.State);
        Assert.Equal(0, weak.RelayStats.ForwardedFrames);
    }

    [Fact]
    public async Task A_member_that_starts_to_relay_announces_it_and_is_used()
    {
        var hub = new InMemoryDiscoveryHub();
        using var relay = TestUtil.NewNode(hub, "relay", relayCapable: false);
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);

        await relay.JoinRoomAsync("mesh");
        await a.JoinRoomAsync("mesh");
        await b.JoinRoomAsync("mesh");
        await TestUtil.WaitFor(() => a.Network.Get(relay.Id) != null && b.Network.Get(relay.Id) != null);
        await Task.Delay(3000);
        Assert.Null(a.Network.Get(b.Id));

        relay.RelayCapableOverride = true;
        relay.AnnounceRelayCapability();
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: true } && b.Network.Get(a.Id) is { IsRelayed: true }, 20_000);
    }

    [Fact]
    public async Task A_path_through_a_member_is_kept_over_a_direct_one_that_uses_the_shared_server()
    {
        var hub = new InMemoryDiscoveryHub();
        using var relay = TestUtil.NewNode(hub, "relay");
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);

        await relay.JoinRoomAsync("mesh");
        await a.JoinRoomAsync("mesh");
        await b.JoinRoomAsync("mesh");
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: true } && b.Network.Get(a.Id) is { IsRelayed: true }, 20_000);

        // Every direct connection now counts as one that runs through the shared server.
        a.Network.IsMetered = c => !c.IsRelayed;
        b.Network.IsMetered = c => !c.IsRelayed;
        a.Mux.OutboundFilter = null; b.Mux.OutboundFilter = null;
        await Task.Delay(6000);
        Assert.True(a.Network.Get(b.Id)!.IsRelayed);
        Assert.True(b.Network.Get(a.Id)!.IsRelayed);
        // Without the rule the direct path wins within seconds; RelayTests covers that with the same set-up.
    }

    private static async Task<(ChatNode Used, ChatNode Spare)> UsedAndSpare(ChatNode a, ChatNode b, ChatNode r1, ChatNode r2)
    {
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: true } && b.Network.Get(a.Id) is { IsRelayed: true }, 30_000);
        var used = a.Network.Get(b.Id)!.RelayedVia == r1.Id ? r1 : r2;
        return (used, used == r1 ? r2 : r1);
    }

    [Fact]
    public async Task When_the_member_relaying_leaves_the_path_moves_to_another_at_once()
    {
        var hub = new InMemoryDiscoveryHub();
        // A long wait before relaying, so that a quick switch can only come from the immediate retry.
        var wait = TimeSpan.FromSeconds(6);
        var r1 = TestUtil.NewNode(hub, "r1", fallbackDelay: wait);
        var r2 = TestUtil.NewNode(hub, "r2", fallbackDelay: wait);
        using var a = TestUtil.NewNode(hub, "a", fallbackDelay: wait);
        using var b = TestUtil.NewNode(hub, "b", fallbackDelay: wait);
        Isolate(a, b);
        foreach (var n in new[] { r1, r2, a, b }) await n.JoinRoomAsync("mesh");

        var (used, spare) = await UsedAndSpare(a, b, r1, r2);
        await TestUtil.WaitFor(() => a.Network.Get(spare.Id) != null && b.Network.Get(spare.Id) != null);

        used.Dispose();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: true } c && c.RelayedVia == spare.Id
                                     && b.Network.Get(a.Id) is { IsRelayed: true } d && d.RelayedVia == spare.Id, 5_000);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        spare.Dispose();
    }

    [Fact]
    public async Task A_standby_path_takes_over_without_the_person_being_lost_and_messages_keep_flowing()
    {
        var hub = new InMemoryDiscoveryHub();
        // A retry would show as the person disappearing for a moment; only a standby avoids that.
        var r1 = TestUtil.NewNode(hub, "r1");
        var r2 = TestUtil.NewNode(hub, "r2");
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        Isolate(a, b);
        await r1.JoinRoomAsync("mesh"); await r2.JoinRoomAsync("mesh");
        var ra = await a.JoinRoomAsync("mesh");
        var rb = await b.JoinRoomAsync("mesh");

        var (used, spare) = await UsedAndSpare(a, b, r1, r2);
        await TestUtil.WaitFor(() => a.Network.HasStandby(b.Id) && b.Network.HasStandby(a.Id), 20_000);

        int lost = 0;
        a.PeerDisconnected += p => { if (p.Id == b.Id) Interlocked.Increment(ref lost); };
        b.PeerDisconnected += p => { if (p.Id == a.Id) Interlocked.Increment(ref lost); };
        var got = new TaskCompletionSource<string>();
        rb.MessageReceived += m => { if (m.Text == "after") got.TrySetResult(m.Text); };

        used.Dispose();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool everPresent = true;
        while (!(a.Network.Get(b.Id) is { } c && c.RelayedVia == spare.Id) && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            everPresent &= a.Network.Get(b.Id) != null;
            await Task.Delay(5);
        }
        Assert.True(a.Network.Get(b.Id)?.RelayedVia == spare.Id, "did not move to the standby");
        Assert.True(everPresent, "the connection to the person disappeared for a moment");

        await ra.SendAsync("after");
        Assert.Equal("after", await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, lost);
        spare.Dispose();
    }

    [Fact]
    public async Task When_the_member_relaying_goes_silent_the_path_moves_to_another_within_seconds()
    {
        var hub = new InMemoryDiscoveryHub();
        var quick = new PeerNetworkOptions
        {
            HelloInterval = TimeSpan.FromMilliseconds(100),
            KeepAliveInterval = TimeSpan.FromMilliseconds(500),
            IdleTimeout = TimeSpan.FromSeconds(3),
        };
        using var r1 = TestUtil.NewNode(hub, "r1", network: quick);
        using var r2 = TestUtil.NewNode(hub, "r2", network: quick);
        using var a = TestUtil.NewNode(hub, "a", network: quick);
        using var b = TestUtil.NewNode(hub, "b", network: quick);
        Isolate(a, b);
        foreach (var n in new[] { r1, r2, a, b }) await n.JoinRoomAsync("mesh");

        var (used, spare) = await UsedAndSpare(a, b, r1, r2);
        await TestUtil.WaitFor(() => a.Network.Get(spare.Id) != null && b.Network.Get(spare.Id) != null);

        used.Mux.OutboundFilter = (_, _) => false; // it vanishes without saying goodbye
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: true } c && c.RelayedVia == spare.Id
                                     && b.Network.Get(a.Id) is { IsRelayed: true } d && d.RelayedVia == spare.Id, 20_000);
    }

    [Fact]
    public async Task An_address_on_the_shared_server_is_not_dialled_again_while_we_are_connected_to_its_owner()
    {
        var hub = new InMemoryDiscoveryHub();
        using var a = TestUtil.NewNode(hub, "a");
        using var b = TestUtil.NewNode(hub, "b");
        await a.JoinRoomAsync("mesh");
        await b.JoinRoomAsync("mesh");
        await TestUtil.WaitFor(() => a.Network.Get(b.Id) is { IsRelayed: false });

        var theirs = a.Network.Get(b.Id)!.RemoteEndPoint;
        var stranger = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9);
        a.SharedServerAddressOverride = ep => ep.Equals(theirs) || ep.Equals(stranger);

        Assert.True(a.IsRedundantServerPath(theirs, null));        // known by address
        Assert.True(a.IsRedundantServerPath(theirs, b.Id));        // known by id
        Assert.False(a.IsRedundantServerPath(stranger, null));     // nobody we know
        a.SharedServerAddressOverride = null;
        Assert.False(a.IsRedundantServerPath(theirs, null));       // not on the server at all: dial as usual
    }

    [Fact]
    public void The_relay_budget_allows_a_burst_then_refuses_and_refills()
    {
        var budget = new RelayBudget(bytesPerSecond: 10_000, bytesPerSender: 4_000, maxPairs: 10, nowMs: 0);
        Assert.True(budget.TryForward("a", "b", 3_000, 0));
        Assert.False(budget.TryForward("a", "b", 3_000, 0)); // over a's share
        Assert.True(budget.TryForward("c", "d", 3_000, 0));  // someone else still gets through
        Assert.True(budget.TryForward("a", "b", 3_000, 1_000)); // a second later it has refilled
        var stats = budget.Stats(1_000);
        Assert.Equal(3, stats.ForwardedFrames);
        Assert.Equal(1, stats.DroppedFrames);
        Assert.Equal(2, stats.ActivePairs);
    }

    [Fact]
    public void The_relay_budget_limits_how_many_pairs_it_serves()
    {
        var budget = new RelayBudget(bytesPerSecond: 1_000_000, bytesPerSender: 1_000_000, maxPairs: 2, nowMs: 0);
        Assert.True(budget.TryForward("a", "b", 100, 0));
        Assert.True(budget.TryForward("c", "d", 100, 0));
        Assert.False(budget.TryForward("e", "f", 100, 0));
        Assert.True(budget.TryForward("b", "a", 100, 0)); // the same pair the other way round is not a new pair
        Assert.True(budget.TryForward("e", "f", 100, 61_000)); // the others went quiet a minute ago
    }
}
