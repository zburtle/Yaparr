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

    public static ChatNode NewNode(InMemoryDiscoveryHub hub, string name, bool relaying = true) => new(new ChatNodeOptions
    {
        EnableRelaying = relaying,
        RelayFallbackDelay = TimeSpan.FromMilliseconds(500),
        DisplayName = name,
        DiscoveryFactory = mux => hub.CreateClient(new IPEndPoint(IPAddress.Loopback, mux.LocalPort)),
        DiscoveryInterval = TimeSpan.FromMilliseconds(300),
        Network = FastNetwork,
    });
}
