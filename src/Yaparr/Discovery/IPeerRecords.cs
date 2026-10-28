using System.Net;
using Yaparr.Identity;

namespace Yaparr.Discovery;

/// <summary>
/// Discovery that can store and fetch "where is this peer right now" for one specific peer, so a newer address
/// replaces an older one instead of piling up beside it.
/// </summary>
public interface IPeerRecords
{
    /// <summary>Publishes where we can be reached. Returns how many nodes stored it.</summary>
    Task<int> PublishAsync(IReadOnlyList<IPEndPoint> endpoints, CancellationToken ct = default);

    /// <summary>The newest address list that <paramref name="peer"/> published, or an empty list.</summary>
    Task<IReadOnlyList<IPEndPoint>> LookupAsync(PeerId peer, CancellationToken ct = default);
}
