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
        Yaparr.Identity.PeerIdentity? identity = null, string? accountStatePath = null, IReadOnlyList<AccountEntry>? accountEntries = null) => new(new ChatNodeOptions
    {
        Identity = identity,
        AccountStatePath = accountStatePath,
        AccountEntries = accountEntries ?? Array.Empty<AccountEntry>(),
        BlockedPeers = blocked ?? Array.Empty<Yaparr.Identity.PeerId>(),
        EnableRelaying = relaying,
        RelayFallbackDelay = fallbackDelay ?? TimeSpan.FromMilliseconds(500),
        DisplayName = name,
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = network ?? FastNetwork,
    }) { RelayCapableOverride = relayCapable };
}
