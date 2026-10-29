using System.Collections.Concurrent;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

// One person can run Yaparr on several devices. Each device is an ordinary peer with a key of its own, so connections,
// relays, voice and delivery all work per device; a certificate from the account key says which person it belongs to.
public sealed partial class ChatNode
{
    // Device to person, for devices that have a certificate. A device that is its own person is not listed.
    private readonly ConcurrentDictionary<PeerId, PeerId> _accounts = new();

    /// <summary>
    /// The id other people know you by: the one to share, and the one direct chats and blocks use. On your first
    /// device it equals <see cref="Id"/>; on a device linked later it does not.
    /// </summary>
    public PeerId AccountId => _identity.AccountId;

    /// <summary>The person a device belongs to; the device itself if we have not seen a certificate for it.</summary>
    internal PeerId AccountOf(PeerId device) => _accounts.TryGetValue(device, out var account) ? account : device;

    /// <summary>Whether a device is this one or another of your own.</summary>
    public bool IsMyDevice(PeerId device) => device == Id || AccountOf(device) == AccountId;

    private void LearnAccount(PeerConnection conn)
    {
        if (conn.RemoteId is { } device && conn.RemoteAccount is { } account && account != device) _accounts[device] = account;
    }

    /// <summary>Your other devices that are connected right now.</summary>
    public IReadOnlyCollection<PeerInfo> MyOtherDevices =>
        _peers.Values.Where(s => s.AccountId == AccountId).Select(s => s.Info).ToList();

    private List<PeerState> DevicesOf(PeerId person) =>
        _peers.Values.Where(s => s.Id == person || s.AccountId == person).ToList();

    private bool HasDevicesOf(PeerId person) => _peers.Values.Any(s => s.Id == person || s.AccountId == person);
}
