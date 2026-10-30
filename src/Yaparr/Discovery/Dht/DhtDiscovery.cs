using System.Net;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Discovery.Dht;

public sealed class DhtDiscovery : IPeerDiscovery, IPeerRecords
{
    private readonly DhtNode _node;
    private readonly PeerIdentity? _identity;
    private long _lastSeq;

    /// <param name="identity">Needed to publish our own address record; without it only lookups work.</param>
    public DhtDiscovery(UdpMux mux, DhtOptions? options = null, PeerIdentity? identity = null)
    {
        _node = new DhtNode(mux, options);
        _identity = identity;
    }

    public DhtNode Node => _node;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long At, IReadOnlyList<IPEndPoint> Peers)> _recent = new();
    // What an announcement just found is reused for a moment; a search a few seconds later asks again, because
    // someone else may have announced since.
    private static readonly long ReuseMs = 3_000;

    public async Task AnnounceAsync(byte[] topic, CancellationToken ct = default)
    {
        var (_, peers) = await _node.AnnounceAndFindAsync(topic, ct).ConfigureAwait(false);
        _recent[Convert.ToHexString(topic)] = (Environment.TickCount64, peers);
    }

    public Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default) =>
        _recent.TryGetValue(Convert.ToHexString(topic), out var r) && Environment.TickCount64 - r.At < ReuseMs
            ? Task.FromResult(r.Peers)
            : _node.GetPeersAsync(topic, ct);

    public async Task<int> PublishAsync(IReadOnlyList<IPEndPoint> endpoints, CancellationToken ct = default)
    {
        if (_identity == null || endpoints.Count == 0) return 0;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The sequence number must go up with every update, also across restarts, so use the clock.
        long seq = Math.Max(Interlocked.Read(ref _lastSeq) + 1, now);
        Interlocked.Exchange(ref _lastSeq, seq);

        var value = PeerRecord.Encode(_identity, endpoints, now);
        var (seed, publicKey) = PeerRecord.DeriveKey(_identity.Id);
        return await _node.PutMutableAsync(seed, publicKey, PeerRecord.SaltBytes, seq, value, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IPEndPoint>> LookupAsync(PeerId peer, CancellationToken ct = default)
    {
        var (_, publicKey) = PeerRecord.DeriveKey(peer);
        var item = await _node.GetMutableAsync(publicKey, PeerRecord.SaltBytes, ct).ConfigureAwait(false);
        if (item == null) return Array.Empty<IPEndPoint>();
        return PeerRecord.Decode(peer, item.Value)?.EndPoints ?? (IReadOnlyList<IPEndPoint>)Array.Empty<IPEndPoint>();
    }

    public void Dispose() => _node.Dispose();
}
