using System.Net;

namespace Yaparr.Discovery;

public sealed class InMemoryDiscoveryHub
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<IPEndPoint>> _topics = new();

    public IPeerDiscovery CreateClient(IPEndPoint self) => new Client(this, self);

    private sealed class Client(InMemoryDiscoveryHub hub, IPEndPoint self) : IPeerDiscovery
    {
        public Task AnnounceAsync(byte[] topic, CancellationToken ct = default)
        {
            lock (hub._gate)
            {
                var key = Convert.ToHexString(topic);
                if (!hub._topics.TryGetValue(key, out var set)) hub._topics[key] = set = new();
                set.Add(self);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default)
        {
            lock (hub._gate)
            {
                var found = hub._topics.TryGetValue(Convert.ToHexString(topic), out var set)
                    ? set.ToList()
                    : new List<IPEndPoint>();
                return Task.FromResult<IReadOnlyList<IPEndPoint>>(found);
            }
        }

        public void Dispose() { }
    }
}
