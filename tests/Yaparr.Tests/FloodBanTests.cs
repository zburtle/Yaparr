using System.Net;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Identity;

namespace Yaparr.Tests;

/// <summary>A peer that was disconnected for flooding is refused for a while when it connects again (issue #30).</summary>
public class FloodBanTests
{
    private static readonly PeerId Mallory = new(new string('a', 64)), Eve = new(new string('b', 64));

    private static FloodBans Bans() => new(new PeerLimits { BanFirst = TimeSpan.FromSeconds(60), BanLongest = TimeSpan.FromMinutes(10) });

    [Fact]
    public void A_ban_lasts_until_its_time_is_up_and_only_for_the_one_banned()
    {
        var bans = Bans();
        Assert.Equal(TimeSpan.FromSeconds(60), bans.Ban(Mallory, 0));
        Assert.True(bans.IsBanned(Mallory, 59_000, out var left));
        Assert.Equal(TimeSpan.FromSeconds(1), left);
        Assert.False(bans.IsBanned(Mallory, 60_000, out _));
        Assert.False(bans.IsBanned(Eve, 1_000, out _));
    }

    [Fact]
    public void Flooding_again_soon_after_a_ban_doubles_it_up_to_the_longest_and_staying_away_starts_it_over()
    {
        var bans = Bans();
        Assert.Equal(TimeSpan.FromSeconds(60), bans.Ban(Mallory, 0));
        // Back at 61 s (the ban ended at 60 s) and floods again: twice as long.
        Assert.Equal(TimeSpan.FromSeconds(120), bans.Ban(Mallory, 61_000));
        Assert.Equal(TimeSpan.FromSeconds(240), bans.Ban(Mallory, 190_000));
        Assert.Equal(TimeSpan.FromSeconds(480), bans.Ban(Mallory, 440_000));
        Assert.Equal(TimeSpan.FromMinutes(10), bans.Ban(Mallory, 1_000_000));    // 960 s would be more than the longest
        Assert.Equal(TimeSpan.FromMinutes(10), bans.Ban(Mallory, 1_650_000));    // stays at the longest

        // Staying away for longer than the last ban's length after it ended starts again from the first.
        Assert.Equal(TimeSpan.FromSeconds(60), bans.Ban(Mallory, 1_650_000 + 600_000 + 600_000 + 1));
    }

    [Fact]
    public void Old_bans_are_forgotten_once_the_next_one_would_start_over_anyway()
    {
        var bans = Bans();
        bans.Ban(Mallory, 0);
        bans.Ban(Eve, 0);
        bans.Forget(60_000);                      // ended, but a repeat would still be doubled
        Assert.Equal(2, bans.Count);
        bans.Forget(120_000);                     // a full ban's length after it ended
        Assert.Equal(0, bans.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), bans.Ban(Mallory, 120_000));
    }

    private static ChatNode Node(string name, PeerLimits? limits = null) => new(new ChatNodeOptions
    {
        DisplayName = name,
        Limits = limits ?? new PeerLimits(),
        DiscoveryFactory = mux => new InMemoryDiscoveryHub().CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = TestUtil.FastNetwork,
    });

    private static IPEndPoint Local(ChatNode node) => new(IPAddress.Loopback, node.Mux.LocalPort);

    // Alice floods bob until bob closes the connection, then comes back; returns how many times bob heard from her (hellos) in all, and whether bob has her banned.
    private static async Task<(int Connected, bool Banned)> FloodAndReturn(PeerLimits limits)
    {
        using var alice = Node("alice");
        using var bob = Node("bob", limits);
        alice.Start(); bob.Start();

        int connected = 0, disconnects = 0;
        bob.PeerConnected += _ => Interlocked.Increment(ref connected);
        bob.PeerDisconnected += _ => Interlocked.Increment(ref disconnects);
        var toBob = await alice.ConnectAsync(Local(bob), bob.Id).WaitAsync(TimeSpan.FromSeconds(10));
        try { for (int i = 0; i < 30; i++) await toBob.SendAsync($"message {i}"); }
        catch (InvalidOperationException) { }   // already cut off
        await TestUtil.WaitFor(() => Volatile.Read(ref disconnects) > 0);
        bool banned = bob.IsBannedForFlooding(alice.Id);

        // She comes back at once and says hello.
        try
        {
            var again = await alice.ConnectAsync(Local(bob), bob.Id).WaitAsync(TimeSpan.FromSeconds(5));
            await again.SendAsync("I am back");
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException) { }
        await Task.Delay(1500);
        return (Volatile.Read(ref connected), banned);
    }

    [Fact]
    public async Task A_peer_disconnected_for_flooding_is_refused_when_it_comes_back()
    {
        var (connected, banned) = await FloodAndReturn(new PeerLimits { MessagesBurst = 1, MessagesPerSecond = 0.001, DropsBeforeClose = 5, BanFirst = TimeSpan.FromSeconds(30) });
        Assert.True(banned);
        Assert.Equal(1, connected);   // the first connection only: the second was closed before it could say hello
    }

    [Fact]
    public async Task With_the_ban_turned_off_a_flooder_can_come_straight_back_as_before()
    {
        var (connected, banned) = await FloodAndReturn(new PeerLimits { MessagesBurst = 1, MessagesPerSecond = 0.001, DropsBeforeClose = 5, BanFirst = TimeSpan.Zero });
        Assert.False(banned);
        Assert.True(connected >= 2, $"connected {connected} times");
    }
}
