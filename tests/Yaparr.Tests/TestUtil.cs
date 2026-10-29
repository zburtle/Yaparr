using System.Net;
using Yaparr.Chat;
using Yaparr.Discovery;
using Yaparr.Transport;

namespace Yaparr.Tests;

internal static class TestUtil
{
    public static async Task WaitFor(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }

    public static PeerNetworkOptions FastNetwork => new() { HelloInterval = TimeSpan.FromMilliseconds(100) };

    // Test nodes have no real network, so by default they count as having a connection that relays.
    public static ChatNode NewNode(InMemoryDiscoveryHub hub, string name, bool relaying = true, bool relayCapable = true,
        TimeSpan? fallbackDelay = null, PeerNetworkOptions? network = null, IReadOnlyCollection<Yaparr.Identity.PeerId>? blocked = null,
        Yaparr.Identity.PeerIdentity? identity = null, string? accountStatePath = null, IReadOnlyList<AccountEntry>? accountEntries = null,
        MediaOptions? media = null) => new(new ChatNodeOptions
    {
        Identity = identity,
        AccountStatePath = accountStatePath,
        AccountEntries = accountEntries ?? Array.Empty<AccountEntry>(),
        MaintenanceInterval = media?.Maintenance ?? TimeSpan.FromSeconds(2),
        MediaFanout = media?.Fanout ?? 3,
        MediaRootFanout = media?.RootFanout ?? 3,
        MediaEdgeTimeout = media?.EdgeTimeout ?? TimeSpan.FromSeconds(6),
        MediaAnnounceInterval = media?.Announce ?? TimeSpan.FromSeconds(10),
        MediaBytesPerSecond = media?.BytesPerSecond ?? 384 * 1024,
        VoiceTreeThreshold = media?.VoiceThreshold ?? 9,
        VoiceRootFanout = media?.VoiceRootFanout ?? 6,
        VoiceTreeRankDelay = media?.VoiceRankDelay ?? TimeSpan.FromSeconds(1),
        BlockedPeers = blocked ?? Array.Empty<Yaparr.Identity.PeerId>(),
        EnableRelaying = relaying,
        RelayFallbackDelay = fallbackDelay ?? TimeSpan.FromMilliseconds(500),
        DisplayName = name,
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = network ?? FastNetwork,
    }) { RelayCapableOverride = relayCapable };
}

/// <summary>Settings for the stream tree in tests; the defaults of a real node are slow for a test.</summary>
internal sealed record MediaOptions
{
    public TimeSpan? Maintenance { get; init; }
    public int? Fanout { get; init; }
    public int? RootFanout { get; init; }
    public TimeSpan? EdgeTimeout { get; init; }
    public TimeSpan? Announce { get; init; }
    public int? BytesPerSecond { get; init; }
    public int? VoiceThreshold { get; init; }
    public int? VoiceRootFanout { get; init; }
    public TimeSpan? VoiceRankDelay { get; init; }

    /// <summary>Quick upkeep, so joining and repairing the tree takes a second or two.</summary>
    public static MediaOptions Fast(int fanout = 3, int rootFanout = 3) => new()
    {
        Maintenance = TimeSpan.FromMilliseconds(200),
        Fanout = fanout,
        RootFanout = rootFanout,
        EdgeTimeout = TimeSpan.FromMilliseconds(1500),
        Announce = TimeSpan.FromSeconds(2),
        VoiceRankDelay = TimeSpan.FromMilliseconds(300),
    };
}
