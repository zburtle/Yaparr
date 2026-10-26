using System.Net;
using Yaparr.Transport;

namespace Yaparr.Discovery.Dht;

public sealed class DhtDiscovery : IPeerDiscovery
{
    private readonly DhtNode _node;

    public DhtDiscovery(UdpMux mux, DhtOptions? options = null) => _node = new DhtNode(mux, options);

    public DhtNode Node => _node;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long At, IReadOnlyList<IPEndPoint> Peers)> _recent = new();
    private static readonly long ReuseMs = 10_000;

    public async Task AnnounceAsync(byte[] topic, CancellationToken ct = default)
    {
        var (_, peers) = await _node.AnnounceAndFindAsync(topic, ct).ConfigureAwait(false);
        _recent[Convert.ToHexString(topic)] = (Environment.TickCount64, peers);
    }

    public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default) =>
        _recent.TryGetValue(Convert.ToHexString(topic), out var r) && Environment.TickCount64 - r.At < ReuseMs
            ? Task.FromResult(r.Peers)
            : _node.GetPeersAsync(topic, ct);

    public void Dispose() => _node.Dispose();
}
