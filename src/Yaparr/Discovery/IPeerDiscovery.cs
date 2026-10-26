using System.Net;

namespace Yaparr.Discovery;

public interface IPeerDiscovery : IDisposable
{
    Task AnnounceAsync(byte[] topic, CancellationToken ct = default);

    Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(byte[] topic, CancellationToken ct = default);
}
