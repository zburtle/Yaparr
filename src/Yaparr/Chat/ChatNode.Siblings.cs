using System.Net;
using Yaparr.Identity;

namespace Yaparr.Chat;

// Your devices find each other without a room in common. Rooms are how people find each other, and so how a device usually finds
// the others of its account: it joins a room they are in. A device that has no rooms yet, such as a phone just linked with a
// QR code (which leaves the room list out), would never meet the others and so never receive the list. Every device announces
// itself on the topic of the account, so each one also looks there, and connects to what it finds. Once connected, the
// certificates show they are the same person, and the account settings (name, rooms, blocks) follow (see ChatNode.AccountSync).
public sealed partial class ChatNode
{
    // With no other device connected the topic is searched every discovery interval; with one connected, now and then, to
    // find another or to come back after a restart.
    private const int SiblingSearchSlowdown = 8;

    // The topic is one anyone can announce on, so a search dials only a few of what it finds.
    private const int MaxSiblingDials = 8;

    /// <summary>Another device of your account is connected right now.</summary>
    public bool HasOwnDeviceConnected => _peers.Values.Any(IsSibling);

    private async Task SiblingLoopAsync()
    {
        var topic = Topics.ForPeer(AccountId);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                bool connected = _peers.Values.Any(IsSibling);
                if (!connected) await FindSiblingsAsync(topic, _cts.Token).ConfigureAwait(false);
                await Task.Delay(connected ? _options.DiscoveryInterval * SiblingSearchSlowdown : _options.DiscoveryInterval, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FindSiblingsAsync(byte[] topic, CancellationToken ct)
    {
        try
        {
            var candidates = new List<IPEndPoint>();
            candidates.AddRange(await Discovery.FindPeersAsync(topic, ct).ConfigureAwait(false));
            // The device that holds the account key published an address record under the account's id.
            if (AccountId != Id) candidates.AddRange(await LookupRecordCachedAsync(AccountId, ct).ConfigureAwait(false));

            long now = Environment.TickCount64;
            foreach (var endPoint in candidates.Distinct().Where(IsSomeoneElse)
                         .Where(ep => now - _directDialed.GetValueOrDefault(ep.ToString()) > 10_000)
                         .OrderBy(_ => Random.Shared.Next()).Take(MaxSiblingDials))
            {
                _directDialed[endPoint.ToString()] = now;
                // Expected is the account: a device of it presents the account's certificate, which is what the handshake checks.
                Network.Connect(endPoint, AccountId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _options.Log?.Invoke($"looking for your other devices failed: {ex.Message}");
        }
    }
}
