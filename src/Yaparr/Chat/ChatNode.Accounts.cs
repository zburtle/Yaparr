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

    /// <summary>This device and the other devices of your account that are connected to it right now.</summary>
    public IReadOnlyList<DeviceInfo> MyDevices =>
        new[] { new DeviceInfo(Id, true, PeerLink.Direct) }
            .Concat(_peers.Values.Where(IsSibling).OrderBy(p => p.Id.Value, StringComparer.Ordinal).Select(p => new DeviceInfo(p.Id, false, GetLink(p.Id))))
            .ToList();

    /// <summary>
    /// Learns from a presence beacon which person a device belongs to, so that someone you blocked is recognised on every device of theirs,
    /// including ones you have never been connected to. Returns true if that was new.
    /// </summary>
    internal bool LearnAccount(StoredPresence beacon)
    {
        if (!SignedPresence.TryGetAccount(beacon, out var device, out var account)) return false;
        if (_accounts.TryGetValue(device, out var known) && known == account) return false;
        _accounts[device] = account;
        RememberOwnDevices(account);
        AccountLearned?.Invoke(device);
        return true;
    }

    /// <summary>The account key of this device, when it is a linked one (it then proves itself in what it sends, see <see cref="MessageEnvelope.WrapReaction"/>).</summary>
    internal byte[]? LinkedDeviceKey => _identity.DeviceCertificate != null ? _identity.AccountPublicKey : null;

    internal byte[]? LinkedDeviceCertificate => _identity.DeviceCertificate;

    /// <summary>
    /// Learns that a device belongs to a person from a message of the device that carries the person's certificate for it, so a reaction from a
    /// linked device that is no longer (or was never) connected still counts as that person's.
    /// </summary>
    internal void LearnAccount(StoredMessage message, ReactionEvent reaction)
    {
        if (reaction.Account == null || reaction.Certificate == null) return;
        try
        {
            var pub = Convert.FromBase64String(message.Pub);
            var accountKey = Convert.FromBase64String(reaction.Account);
            if (!PeerIdentity.VerifyDevice(accountKey, pub, Convert.FromBase64String(reaction.Certificate))) return;
            var device = PeerId.FromPublicKey(pub);
            var account = PeerId.FromPublicKey(accountKey);
            if (device.Value != message.From || device == account) return;
            if (_accounts.TryGetValue(device, out var known) && known == account) return;
            _accounts[device] = account;
            RememberOwnDevices(account);
            AccountLearned?.Invoke(device);
        }
        catch (FormatException) { }
    }

    private void LearnAccount(PeerConnection conn)
    {
        if (conn.RemoteId is { } device && conn.RemoteAccount is { } account && account != device
            && (!_accounts.TryGetValue(device, out var known) || known != account))
        {
            _accounts[device] = account;
            RememberOwnDevices(account);
            AccountLearned?.Invoke(device);
        }
    }

    // Which devices are yours is kept between runs. Without it, what one of them sent while this device was closed shows as someone else's until that
    // device is next seen (its certificate only arrives with it).
    private string? OwnDevicesPath => _options.PeerCachePath is { Length: > 0 } cache ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cache))!, "own-devices.json") : null;

    private readonly object _ownDevicesGate = new();

    private void LoadOwnDevices()
    {
        if (OwnDevicesPath is not { } path || !File.Exists(path)) return;
        try
        {
            foreach (var id in System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new())
                if (IsValidPeerId(id) && new PeerId(id) != AccountId) _accounts[new PeerId(id)] = AccountId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { _options.Log?.Invoke("own devices: could not read " + path + ": " + ex.Message); }
    }

    private void RememberOwnDevices(PeerId account)
    {
        if (account != AccountId || OwnDevicesPath is not { } path) return;
        lock (_ownDevicesGate)
        {
            try
            {
                var ids = _accounts.Where(kv => kv.Value == AccountId).Select(kv => kv.Key.Value).OrderBy(v => v, StringComparer.Ordinal).ToList();
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(ids));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _options.Log?.Invoke("own devices: could not save: " + ex.Message); }
        }
    }

    /// <summary>
    /// Raised when it becomes known which person a device belongs to. Messages from a device can arrive before that is known, so a
    /// message that looked like someone else's may turn out to be yours (<see cref="IsMyDevice"/>).
    /// </summary>
    public event Action<PeerId>? AccountLearned;

    /// <summary>Your other devices that are connected right now.</summary>
    public IReadOnlyCollection<PeerInfo> MyOtherDevices =>
        _peers.Values.Where(s => s.AccountId == AccountId).Select(s => s.Info).ToList();

    private List<PeerState> DevicesOf(PeerId person) =>
        _peers.Values.Where(s => s.Id == person || s.AccountId == person).ToList();

    private bool HasDevicesOf(PeerId person) => _peers.Values.Any(s => s.Id == person || s.AccountId == person);
}
