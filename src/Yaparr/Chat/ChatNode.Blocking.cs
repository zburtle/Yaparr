using System.Collections.Concurrent;
using Yaparr.Identity;

namespace Yaparr.Chat;

// A personal block: nothing from that person reaches you, and you do nothing for them. It is only your own view of
// the room. Everyone else still sees them, and your app still passes the room's messages along as usual.
public sealed partial class ChatNode
{
    private readonly ConcurrentDictionary<PeerId, byte> _blocked = new();

    /// <summary>The people you have blocked.</summary>
    public IReadOnlyCollection<PeerId> BlockedPeers => _blocked.Keys.ToList();

    /// <summary>Whether a device, or the person it belongs to, is blocked.</summary>
    public bool IsBlocked(PeerId peer) => _blocked.ContainsKey(peer) || (_accounts.TryGetValue(peer, out var account) && _blocked.ContainsKey(account));

    /// <summary>Raised when someone is blocked (true) or unblocked (false), so the app can remember the list.</summary>
    public event Action<PeerId, bool>? BlockChanged;

    /// <summary>
    /// Blocks someone, with all of their devices. Their messages, pictures and voice no longer reach you, they leave
    /// your member lists and voice channel, and your connection to them is closed and not opened again. You also stop
    /// relaying for them. Pass either a device id or the person's account id; the person is what is remembered.
    /// </summary>
    public void Block(PeerId peer) => Block(peer, announce: true);

    private void Block(PeerId peer, bool announce)
    {
        var person = AccountOf(peer);
        if (peer == Id || IsMyDevice(peer) || person == AccountId) throw new ArgumentException("You cannot block yourself.", nameof(peer));
        if (!_blocked.TryAdd(person, 0)) return;
        _options.Log?.Invoke($"blocked {person.Short}");
        foreach (var device in _accounts.Where(kv => kv.Value == person).Select(kv => kv.Key).Append(person))
            Network.Get(device)?.Close(notifyPeer: true);
        RefreshBlockedViews();
        BlockChanged?.Invoke(person, true);
        if (announce) NoteBlockChanged(person, true);
    }

    public void Unblock(PeerId peer) => Unblock(peer, announce: true);

    private void Unblock(PeerId peer, bool announce)
    {
        var person = AccountOf(peer);
        // A block placed on a single device before we knew whose it was is lifted too.
        bool removed = _blocked.TryRemove(person, out _);
        removed |= _blocked.TryRemove(peer, out _);
        if (!removed) return;
        _options.Log?.Invoke($"unblocked {person.Short}");
        RefreshBlockedViews();
        BlockChanged?.Invoke(person, false);
        if (announce) NoteBlockChanged(person, false);
    }

    private void RefreshBlockedViews()
    {
        foreach (var room in _rooms.Values)
        {
            RefreshIndirect(room);
            VoiceRosterChanged(room);
        }
    }
}
